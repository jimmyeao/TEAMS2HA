//! DPAPI protection for the MQTT password at rest.
//!
//! `settings.json` previously stored the password in plaintext. The WPF app encrypted it
//! with DPAPI via `CryptoHelper.cs`, and `Cargo.toml` has carried the
//! `Win32_Security_Cryptography` feature since the Rust port began — for this, which was
//! started and never finished.
//!
//! # Format on disk
//!
//! `dpapi:` + base64(`CryptProtectData(utf-8 password)`).
//!
//! A value **without** the prefix is treated as legacy plaintext and returned as-is, so
//! existing installs keep working and are silently re-encrypted on the next save. The
//! prefix is what makes migration detectable — a bare base64 blob would be ambiguous
//! against a password that happens to look like base64.
//!
//! # Scope and failure behaviour
//!
//! DPAPI is used at **current-user** scope, so the blob is bound to the Windows account.
//! Copying `settings.json` to another user or machine yields a value that cannot be
//! decrypted; that is reported as an empty password rather than an error, so the app
//! still starts and the user can simply re-enter it.
//!
//! Encryption failure falls back to storing plaintext with a warning: refusing to save
//! settings at all would be a worse outcome than the status quo this replaces.

use base64::{engine::general_purpose::STANDARD as BASE64, Engine as _};

/// Marks a stored value as DPAPI-protected. Anything else is legacy plaintext.
const PREFIX: &str = "dpapi:";

/// Encrypt for storage. Returns the value to write to `settings.json`.
pub fn protect(plain: &str) -> String {
    if plain.is_empty() {
        return String::new();
    }
    match protect_impl(plain.as_bytes()) {
        Some(bytes) => format!("{PREFIX}{}", BASE64.encode(bytes)),
        None => {
            log::warn!("Could not DPAPI-encrypt the MQTT password; storing it as plaintext");
            plain.to_string()
        }
    }
}

/// Decrypt a value read from `settings.json`. Legacy plaintext passes through unchanged.
pub fn unprotect(stored: &str) -> String {
    let Some(encoded) = stored.strip_prefix(PREFIX) else {
        // Legacy plaintext from before this was implemented — re-encrypted on next save.
        return stored.to_string();
    };

    let raw = match BASE64.decode(encoded) {
        Ok(r) => r,
        Err(e) => {
            log::warn!("Stored MQTT password is not valid base64 ({e}); treating as empty");
            return String::new();
        }
    };

    match unprotect_impl(&raw) {
        Some(bytes) => String::from_utf8_lossy(&bytes).into_owned(),
        None => {
            log::warn!(
                "Could not decrypt the stored MQTT password. This is expected if settings.json \
                 was copied from another user or machine — please re-enter it."
            );
            String::new()
        }
    }
}

#[cfg(windows)]
fn protect_impl(data: &[u8]) -> Option<Vec<u8>> {
    use windows::Win32::Security::Cryptography::{CryptProtectData, CRYPT_INTEGER_BLOB};

    unsafe {
        let input = CRYPT_INTEGER_BLOB {
            cbData: data.len() as u32,
            pbData: data.as_ptr() as *mut u8,
        };
        let mut output = CRYPT_INTEGER_BLOB::default();

        CryptProtectData(&input, None, None, None, None, 0, &mut output).ok()?;
        Some(take_blob(&mut output))
    }
}

#[cfg(windows)]
fn unprotect_impl(data: &[u8]) -> Option<Vec<u8>> {
    use windows::Win32::Security::Cryptography::{CryptUnprotectData, CRYPT_INTEGER_BLOB};

    unsafe {
        let input = CRYPT_INTEGER_BLOB {
            cbData: data.len() as u32,
            pbData: data.as_ptr() as *mut u8,
        };
        let mut output = CRYPT_INTEGER_BLOB::default();

        CryptUnprotectData(&input, None, None, None, None, 0, &mut output).ok()?;
        Some(take_blob(&mut output))
    }
}

/// Copy a blob DPAPI allocated for us, then release it.
///
/// CryptProtectData/CryptUnprotectData allocate `pbData` with LocalAlloc and the caller
/// owns it; without the LocalFree this leaks on every save and load.
#[cfg(windows)]
unsafe fn take_blob(
    blob: &mut windows::Win32::Security::Cryptography::CRYPT_INTEGER_BLOB,
) -> Vec<u8> {
    use windows::Win32::Foundation::{HLOCAL, LocalFree};

    let out = std::slice::from_raw_parts(blob.pbData, blob.cbData as usize).to_vec();
    let _ = LocalFree(Some(HLOCAL(blob.pbData as *mut _)));
    blob.pbData = std::ptr::null_mut();
    out
}

#[cfg(not(windows))]
fn protect_impl(_data: &[u8]) -> Option<Vec<u8>> {
    None
}

#[cfg(not(windows))]
fn unprotect_impl(_data: &[u8]) -> Option<Vec<u8>> {
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn empty_stays_empty() {
        assert_eq!(protect(""), "");
        assert_eq!(unprotect(""), "");
    }

    #[test]
    fn legacy_plaintext_passes_through() {
        // Settings written before this existed have no prefix and must keep working.
        assert_eq!(unprotect("hunter2"), "hunter2");
    }

    #[cfg(windows)]
    #[test]
    fn round_trips() {
        let secret = "s3cr3t-p@ssw0rd åäö";
        let stored = protect(secret);
        assert!(stored.starts_with(PREFIX), "expected prefix, got {stored}");
        assert!(!stored.contains(secret), "ciphertext must not contain the plaintext");
        assert_eq!(unprotect(&stored), secret);
    }

    #[cfg(windows)]
    #[test]
    fn corrupt_ciphertext_yields_empty_not_garbage() {
        assert_eq!(unprotect("dpapi:not-base64!!"), "");
        assert_eq!(unprotect("dpapi:YWJjZGVm"), "");
    }
}
