use std::{
    ffi::OsString,
    fs,
    io::{self, Read},
    mem::{size_of, zeroed},
    os::windows::{
        ffi::{OsStrExt, OsStringExt},
        fs::OpenOptionsExt,
    },
    path::{Path, PathBuf},
    ptr::null_mut,
    sync::{mpsc, OnceLock},
    thread,
    time::{Duration, SystemTime, UNIX_EPOCH},
};

use hbb_common::anyhow::{ensure, Context, Result};
use serde_derive::Deserialize;
use uuid::Uuid;
use winapi::{
    shared::{
        minwindef::{BOOL, FALSE},
        ntdef::HANDLE,
    },
    um::{
        handleapi::CloseHandle,
        processthreadsapi::{
            CreateProcessAsUserW, GetCurrentProcess, OpenProcess, OpenProcessToken,
            TerminateProcess, PROCESS_INFORMATION, STARTUPINFOW,
        },
        securitybaseapi::{DuplicateTokenEx, SetTokenInformation},
        synchapi::WaitForSingleObject,
        winbase::{DETACHED_PROCESS, WAIT_OBJECT_0},
        winnt::{
            SecurityImpersonation, TokenPrimary, TokenSessionId, SYNCHRONIZE,
            TOKEN_ADJUST_SESSIONID, TOKEN_ASSIGN_PRIMARY, TOKEN_DUPLICATE, TOKEN_QUERY,
        },
    },
};
use windows::Win32::System::SystemInformation::GetSystemDirectoryW;
use windows_service::{
    define_windows_service,
    service::{
        ServiceControl, ServiceControlAccept, ServiceExitCode, ServiceState, ServiceStatus,
        ServiceType,
    },
    service_control_handler::{self, ServiceControlHandlerResult},
    service_dispatcher,
};
use winreg::{enums::HKEY_LOCAL_MACHINE, RegKey};

use super::{configure_network, host_identity, initialize_host, Network};
use crate::platform::windows::{get_current_session_id, is_root};

const SERVICE: &str = "TboxRemote";
const POLL: Duration = Duration::from_millis(500);
static CONSOLE_ATTEMPT: OnceLock<Uuid> = OnceLock::new();

pub(crate) fn active_worker() -> bool {
    CONSOLE_ATTEMPT.get().is_some()
}

pub(crate) fn revoke_consent() {
    let Some(attempt) = CONSOLE_ATTEMPT.get() else {
        return;
    };
    let Ok(directory) = state_directory() else {
        return;
    };
    let Ok(_lock) = fs::OpenOptions::new()
        .read(true)
        .write(true)
        .share_mode(0)
        .open(directory.join("configuration.lock"))
    else {
        return;
    };
    let active = directory.join("active.json");
    if read_json::<Consent>(&active).is_ok_and(|consent| consent.attempt == *attempt)
        && fs::remove_file(active).is_ok()
    {
        if let Ok(marker) = RegKey::predef(HKEY_LOCAL_MACHINE).open_subkey_with_flags(
            "SYSTEM\\CurrentControlSet\\Control\\TboxRemote",
            winreg::enums::KEY_SET_VALUE,
        ) {
            let _ = marker.delete_value("DeviceId");
        }
    }
}

#[derive(Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
struct Policy {
    origin: String,
    network: Network,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Consent {
    attempt: Uuid,
    device_id: Uuid,
    credential: String,
    expires_at: u64,
    policy: Policy,
}

fn read_json<T: serde::de::DeserializeOwned>(path: &Path) -> Result<T> {
    // State is small and only the administrator or LocalSystem may write it.
    let mut bytes = Vec::new();
    fs::File::open(path)?.take(4097).read_to_end(&mut bytes)?;
    ensure!(
        bytes.len() <= 4096,
        "Tbox remote service configuration is too large"
    );
    Ok(serde_json::from_slice(&bytes)?)
}

fn state_directory() -> Result<PathBuf> {
    let mut buffer = [0u16; 260];
    let count = unsafe { GetSystemDirectoryW(Some(&mut buffer)) } as usize;
    ensure!(
        count > 0 && count < buffer.len(),
        "Windows system directory is unavailable"
    );
    Ok(PathBuf::from(OsString::from_wide(&buffer[..count]))
        .join("config/systemprofile/AppData/Local/TboxRemoteHost"))
}

fn write_status(directory: &Path, name: &str, status: &str) -> io::Result<()> {
    fs::write(directory.join(name), status)
}

fn requested_consent(directory: &Path) -> Option<Consent> {
    if let Ok(active) = read_json(&directory.join("active.json")) {
        return Some(active);
    }
    let pending: Consent = read_json(&directory.join("pending.json")).ok()?;
    let now = SystemTime::now().duration_since(UNIX_EPOCH).ok()?.as_secs();
    (now < pending.expires_at && pending.expires_at - now <= 120).then_some(pending)
}

struct OwnedHandle(HANDLE);

impl OwnedHandle {
    fn exited(&self) -> bool {
        unsafe { WaitForSingleObject(self.0, 0) == WAIT_OBJECT_0 }
    }
}

impl Drop for OwnedHandle {
    fn drop(&mut self) {
        unsafe { CloseHandle(self.0) };
    }
}

fn windows_result(result: BOOL, operation: &'static str) -> Result<()> {
    if result == FALSE {
        return Err(io::Error::last_os_error()).context(operation);
    }
    Ok(())
}

fn error_status(error: &hbb_common::anyhow::Error) -> String {
    // Context messages are fixed and never include consent data.
    match error
        .downcast_ref::<io::Error>()
        .and_then(io::Error::raw_os_error)
    {
        Some(code) => format!("{error} (Windows error {code})"),
        None => error.to_string(),
    }
}

fn launch_console(executable: &Path, session: u32, service_pid: u32) -> Result<OwnedHandle> {
    let parent = executable
        .parent()
        .context("Could not locate the remote worker directory")?;
    let application: Vec<u16> = executable
        .as_os_str()
        .encode_wide()
        .chain(Some(0))
        .collect();
    let directory: Vec<u16> = parent.as_os_str().encode_wide().chain(Some(0)).collect();
    let mut arguments = OsString::from("\"");
    arguments.push(executable.as_os_str());
    arguments.push(format!("\" --server --tbox-console {service_pid}"));
    let mut command: Vec<u16> = arguments.encode_wide().chain(Some(0)).collect();
    let mut desktop: Vec<u16> = "winsta0\\default".encode_utf16().chain(Some(0)).collect();

    unsafe {
        // The signed service already runs as LocalSystem. Use its own token for its
        // worker so launch does not depend on opening Windows sign-in processes.
        let mut source = null_mut();
        windows_result(
            OpenProcessToken(GetCurrentProcess(), TOKEN_DUPLICATE, &mut source),
            "Could not open the Tbox service token",
        )?;
        let source = OwnedHandle(source);
        let mut console = null_mut();
        windows_result(
            DuplicateTokenEx(
                source.0,
                TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ADJUST_SESSIONID,
                null_mut(),
                SecurityImpersonation,
                TokenPrimary,
                &mut console,
            ),
            "Could not prepare the Tbox console token",
        )?;
        let console = OwnedHandle(console);
        let mut session_id = session;
        windows_result(
            SetTokenInformation(
                console.0,
                TokenSessionId,
                (&mut session_id as *mut u32).cast(),
                size_of::<u32>() as u32,
            ),
            "Could not select the Windows console session",
        )?;

        let mut startup: STARTUPINFOW = zeroed();
        startup.cb = size_of::<STARTUPINFOW>() as u32;
        startup.lpDesktop = desktop.as_mut_ptr();
        let mut process: PROCESS_INFORMATION = zeroed();
        windows_result(
            CreateProcessAsUserW(
                console.0,
                application.as_ptr(),
                command.as_mut_ptr(),
                null_mut(),
                null_mut(),
                FALSE,
                DETACHED_PROCESS,
                null_mut(),
                directory.as_ptr(),
                &mut startup,
                &mut process,
            ),
            "Windows did not start the console worker",
        )?;
        CloseHandle(process.hThread);
        Ok(OwnedHandle(process.hProcess))
    }
}

struct Worker {
    process: OwnedHandle,
    session: u32,
    attempt: Uuid,
}

impl Drop for Worker {
    fn drop(&mut self) {
        // Use the launch handle so an unrelated process cannot be stopped via PID reuse.
        unsafe {
            TerminateProcess(self.process.0, 0);
            WaitForSingleObject(self.process.0, 2000);
        }
    }
}

define_windows_service!(service_entry, service_main);

pub(crate) fn dispatch() -> Result<()> {
    ensure!(is_root(), "Tbox remote service requires LocalSystem");
    service_dispatcher::start(SERVICE, service_entry)?;
    Ok(())
}

fn service_main(_: Vec<OsString>) {
    if let Err(error) = supervise() {
        hbb_common::log::error!("Tbox remote service stopped: {error}");
    }
}

fn supervise() -> Result<()> {
    let directory = state_directory()?;
    let executable = std::env::current_exe()?;
    let policy: Policy = read_json(&executable.with_file_name("tbox-host-policy.json"))?;
    let service_pid = std::process::id();
    let (stop, controls) = mpsc::channel();
    let status = service_control_handler::register(SERVICE, move |event| match event {
        ServiceControl::Interrogate => ServiceControlHandlerResult::NoError,
        ServiceControl::Stop | ServiceControl::Shutdown => {
            let _ = stop.send(());
            ServiceControlHandlerResult::NoError
        }
        _ => ServiceControlHandlerResult::NotImplemented,
    })?;
    let mut state = ServiceStatus {
        service_type: ServiceType::OWN_PROCESS,
        current_state: ServiceState::Running,
        controls_accepted: ServiceControlAccept::STOP | ServiceControlAccept::SHUTDOWN,
        exit_code: ServiceExitCode::Win32(0),
        checkpoint: 0,
        wait_hint: Duration::ZERO,
        process_id: None,
    };
    status.set_service_status(ServiceStatus { ..state })?;

    let mut worker: Option<Worker> = None;
    let mut last_launch = None;
    loop {
        let session = get_current_session_id(false);
        let attempt = requested_consent(&directory)
            .filter(|consent| consent.policy == policy)
            .map(|consent| consent.attempt);
        if worker.as_ref().is_some_and(|previous| {
            previous.session != session
                || Some(previous.attempt) != attempt
                || previous.process.exited()
        }) {
            worker = None;
        }
        if let (None, Some(attempt)) = (&worker, attempt) {
            let stage = if session == u32::MAX || session == 0 {
                "Waiting for a console session".to_owned()
            } else {
                match launch_console(&executable, session, service_pid) {
                    Ok(process) => {
                        worker = Some(Worker {
                            process,
                            session,
                            attempt,
                        });
                        "Started the console worker".to_owned()
                    }
                    Err(error) => error_status(&error),
                }
            };
            let launch = (attempt, session, stage);
            if last_launch.as_ref() != Some(&launch)
                && write_status(&directory, "service-status.txt", &launch.2).is_ok()
            {
                last_launch = Some(launch);
            }
        }
        if controls.recv_timeout(POLL) != Err(mpsc::RecvTimeoutError::Timeout) {
            break;
        }
    }
    drop(worker);
    state.current_state = ServiceState::Stopped;
    state.controls_accepted = ServiceControlAccept::empty();
    status.set_service_status(state)?;
    Ok(())
}

pub(crate) fn start_console(service_pid: u32) -> Result<()> {
    ensure!(
        is_root() && service_pid != 0,
        "Tbox remote console requires the system service"
    );
    let directory = state_directory()?;
    let result = prepare_console(&directory, service_pid);
    if let Err(error) = &result {
        let _ = write_status(&directory, "console-status.txt", &error_status(error));
    }
    result
}

fn prepare_console(directory: &Path, service_pid: u32) -> Result<()> {
    let service = unsafe { OpenProcess(SYNCHRONIZE, 0, service_pid) };
    if service.is_null() {
        return Err(io::Error::last_os_error())
            .context("Could not monitor the Tbox remote service");
    }
    // A service crash must also close the console host and any existing session.
    let service = service as usize;
    let monitor = thread::spawn(move || {
        let service = OwnedHandle(service as HANDLE);
        unsafe { WaitForSingleObject(service.0, u32::MAX) };
        std::process::exit(0);
    });
    drop(monitor);

    let policy_path = std::env::current_exe()
        .context("Could not locate the Tbox remote console worker")?
        .with_file_name("tbox-host-policy.json");
    let policy: Policy =
        read_json(&policy_path).context("Could not read the installed remote policy")?;
    let consent = requested_consent(directory).context("Tbox remote host consent is absent")?;
    ensure!(
        consent.policy == policy,
        "Tbox remote host needs consent for the installed policy"
    );
    let authorization =
        tbox_remote::HostAuthorization::new(&policy.origin, consent.device_id, consent.credential)
            .context("Could not initialize the console authorization")?;
    configure_network(&policy.network).context("Could not configure the console connection")?;
    let identity = host_identity().context("Could not load the console identity")?;
    let active_path = directory.join("active.json");
    let is_approved =
        || read_json::<Consent>(&active_path).is_ok_and(|active| active.attempt == consent.attempt);
    if !is_approved() {
        let identity_path = directory.join(format!("identity-{}.json", consent.attempt));
        let temporary = identity_path.with_extension("pending");
        let identity_json =
            serde_json::to_vec(&identity).context("Could not encode the console identity")?;
        fs::write(&temporary, identity_json)
            .context("Could not write the pending console identity")?;
        fs::rename(&temporary, &identity_path).context("Could not publish the console identity")?;
        // The signed account registration completes before the helper commits local consent.
        while !is_approved() {
            ensure!(
                requested_consent(directory)
                    .is_some_and(|current| current.attempt == consent.attempt),
                "Tbox remote host consent was cancelled"
            );
            thread::sleep(POLL);
        }
    }
    initialize_host(authorization, identity)?;
    CONSOLE_ATTEMPT
        .set(consent.attempt)
        .map_err(|_| hbb_common::anyhow::anyhow!("Tbox remote console already initialized"))?;
    crate::start_server(true, false);
    Ok(())
}
