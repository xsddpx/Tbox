use std::{
    ffi::OsString,
    fs,
    io::{self, Read},
    os::windows::{ffi::OsStringExt, fs::OpenOptionsExt},
    path::{Path, PathBuf},
    sync::{mpsc, OnceLock},
    thread,
    time::{Duration, SystemTime, UNIX_EPOCH},
};

use hbb_common::anyhow::{ensure, Context, Result};
use serde_derive::Deserialize;
use uuid::Uuid;
use winapi::{
    shared::ntdef::HANDLE,
    um::{
        handleapi::CloseHandle,
        processthreadsapi::{OpenProcess, TerminateProcess},
        synchapi::WaitForSingleObject,
        winbase::WAIT_OBJECT_0,
        winnt::SYNCHRONIZE,
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
use crate::platform::windows::{get_current_session_id, is_root, launch_privileged_process};

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

struct Process(HANDLE);

impl Process {
    fn exited(&self) -> bool {
        unsafe { WaitForSingleObject(self.0, 0) == WAIT_OBJECT_0 }
    }
}

impl Drop for Process {
    fn drop(&mut self) {
        unsafe { CloseHandle(self.0) };
    }
}

struct Worker {
    process: Process,
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
    let executable = executable.to_str().context("Invalid Tbox service path")?;
    let service_pid = std::process::id();
    let command = format!("\"{executable}\" --server --tbox-console {service_pid}");
    ensure!(
        command.encode_utf16().count() < 260,
        "Tbox remote executable path is too long"
    );
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
                "Waiting for a console session"
            } else {
                match launch_privileged_process(session, &command) {
                    Ok(process) if !process.is_null() => {
                        worker = Some(Worker {
                            process: Process(process),
                            session,
                            attempt,
                        });
                        "Started the console worker"
                    }
                    _ => "Windows did not start the console worker",
                }
            };
            if last_launch != Some((attempt, session, stage))
                && write_status(&directory, "service-status.txt", stage).is_ok()
            {
                last_launch = Some((attempt, session, stage));
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
        // Persist the fixed stage and the Windows error code without recording enrollment data.
        let code = error
            .downcast_ref::<io::Error>()
            .and_then(io::Error::raw_os_error);
        let _ = write_status(
            &directory,
            "console-status.txt",
            &match code {
                Some(code) => format!("{error} (Windows error {code})"),
                None => error.to_string(),
            },
        );
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
        let service = Process(service as HANDLE);
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
