use base64::Engine;
use base64::engine::general_purpose::URL_SAFE_NO_PAD;
use hmac::{Hmac, Mac};
use serde::{Deserialize, Serialize};
use sha2::Sha256;
use thiserror::Error;
use uuid::Uuid;

use crate::{canonical_json, canonical_json_without, protocol::StagedObjectReceipt};

type HmacSha256 = Hmac<Sha256>;

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
pub struct TicketClaims {
    pub v: u32,
    pub op_id: String,
    /// Opaque control-plane actor ID. Python emits this as a string.
    pub user_id: String,
    pub action: String,
    pub direction: String,
    pub object_id: String,
    pub object_kind: String,
    pub expected_version: i64,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub size: Option<u64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub sha256: Option<String>,
    pub exp: i64,
    pub nonce: String,
}

#[derive(Debug, Error)]
pub enum TicketError {
    #[error("ticket has an invalid format")]
    Format,
    #[error("ticket payload is not valid canonical JSON")]
    Payload,
    #[error("ticket signature is invalid")]
    Signature,
    #[error("ticket is expired")]
    Expired,
    #[error("ticket claims are invalid")]
    Claims,
    #[error("ticket serialization failed: {0}")]
    Serialization(#[from] serde_json::Error),
    #[error("HMAC initialization failed")]
    Hmac,
}

pub fn sign_ticket(claims: &TicketClaims, secret: &[u8]) -> Result<String, TicketError> {
    validate_claims(claims, i64::MIN)?;
    let payload = canonical_json(claims)?;
    let signature = sign_bytes(secret, &payload)?;
    Ok(format!(
        "v1.{}.{}",
        URL_SAFE_NO_PAD.encode(payload),
        URL_SAFE_NO_PAD.encode(signature)
    ))
}

pub fn verify_ticket(
    token: &str,
    secret: &[u8],
    now_unix_seconds: i64,
) -> Result<TicketClaims, TicketError> {
    verify_ticket_inner(token, secret, Some(now_unix_seconds))
}

/// Verify signature and structure for an already-created upload session.
/// Callers must first prove that the signed ticket matches a persisted receiving operation.
pub fn verify_ticket_for_resume(token: &str, secret: &[u8]) -> Result<TicketClaims, TicketError> {
    verify_ticket_inner(token, secret, None)
}

fn verify_ticket_inner(
    token: &str,
    secret: &[u8],
    now_unix_seconds: Option<i64>,
) -> Result<TicketClaims, TicketError> {
    let mut parts = token.split('.');
    if parts.next() != Some("v1") {
        return Err(TicketError::Format);
    }
    let payload = URL_SAFE_NO_PAD
        .decode(parts.next().ok_or(TicketError::Format)?)
        .map_err(|_| TicketError::Format)?;
    let signature = URL_SAFE_NO_PAD
        .decode(parts.next().ok_or(TicketError::Format)?)
        .map_err(|_| TicketError::Format)?;
    if parts.next().is_some() {
        return Err(TicketError::Format);
    }
    verify_bytes(secret, &payload, &signature)?;

    let claims: TicketClaims =
        serde_json::from_slice(&payload).map_err(|_| TicketError::Payload)?;
    if canonical_json(&claims).map_err(|_| TicketError::Payload)? != payload {
        return Err(TicketError::Payload);
    }
    validate_claims(&claims, now_unix_seconds.unwrap_or(i64::MIN))?;
    Ok(claims)
}

pub fn sign_receipt(
    mut receipt: StagedObjectReceipt,
    secret: &[u8],
) -> Result<StagedObjectReceipt, TicketError> {
    receipt.mac.clear();
    let bytes = canonical_json_without(&receipt, "mac")?;
    receipt.mac = URL_SAFE_NO_PAD.encode(sign_bytes(secret, &bytes)?);
    Ok(receipt)
}

pub fn verify_receipt(receipt: &StagedObjectReceipt, secret: &[u8]) -> Result<(), TicketError> {
    if receipt.v != 1 || receipt.mac.is_empty() {
        return Err(TicketError::Claims);
    }
    let provided = URL_SAFE_NO_PAD
        .decode(&receipt.mac)
        .map_err(|_| TicketError::Format)?;
    let payload = canonical_json_without(receipt, "mac")?;
    verify_bytes(secret, &payload, &provided)
}

fn validate_claims(claims: &TicketClaims, now: i64) -> Result<(), TicketError> {
    if claims.v != 1
        || !matches!(claims.action.as_str(), "upload" | "download")
        || claims.action != claims.direction
        || claims.user_id.trim().is_empty()
        || !matches!(claims.object_kind.as_str(), "image" | "video" | "other" | "thumbnail")
        || claims.expected_version < 0
        || claims.nonce.is_empty()
        || claims.nonce.len() > 128
        || Uuid::parse_str(&claims.op_id).is_err()
        || Uuid::parse_str(&claims.object_id).is_err()
        || claims.sha256.as_ref().is_some_and(|hash| !is_sha256(hash))
    {
        return Err(TicketError::Claims);
    }
    if now != i64::MIN && claims.exp <= now {
        return Err(TicketError::Expired);
    }
    Ok(())
}

fn is_sha256(value: &str) -> bool {
    value.len() == 64 && value.bytes().all(|byte| byte.is_ascii_hexdigit())
}

fn sign_bytes(secret: &[u8], bytes: &[u8]) -> Result<Vec<u8>, TicketError> {
    let mut mac = HmacSha256::new_from_slice(secret).map_err(|_| TicketError::Hmac)?;
    mac.update(bytes);
    Ok(mac.finalize().into_bytes().to_vec())
}

fn verify_bytes(secret: &[u8], bytes: &[u8], signature: &[u8]) -> Result<(), TicketError> {
    let mut mac = HmacSha256::new_from_slice(secret).map_err(|_| TicketError::Hmac)?;
    mac.update(bytes);
    mac.verify_slice(signature)
        .map_err(|_| TicketError::Signature)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ticket_round_trip_and_expiry() {
        let claims = TicketClaims {
            v: 1,
            op_id: "a5c083b5-8ab7-4087-a6c6-788c90e2f224".into(),
            user_id: "7".into(),
            action: "upload".into(),
            direction: "upload".into(),
            object_id: "00112233-4455-6677-8899-aabbccddeeff".into(),
            object_kind: "video".into(),
            expected_version: 3,
            size: Some(17),
            sha256: Some("a".repeat(64)),
            exp: 100,
            nonce: "n-1".into(),
        };
        let token = sign_ticket(&claims, b"test secret").unwrap();
        assert_eq!(verify_ticket(&token, b"test secret", 99).unwrap(), claims);
        assert!(matches!(
            verify_ticket(&token, b"test secret", 100),
            Err(TicketError::Expired)
        ));
        assert!(matches!(
            verify_ticket(&token, b"wrong", 99),
            Err(TicketError::Signature)
        ));
    }

    #[test]
    fn receipt_mac_does_not_include_mac_field() {
        let receipt = StagedObjectReceipt {
            v: 1,
            op_id: "a5c083b5-8ab7-4087-a6c6-788c90e2f224".into(),
            object_id: "00112233-4455-6677-8899-aabbccddeeff".into(),
            size: 17,
            sha256: "a".repeat(64),
            staged_at: "2026-10-16T12:34:56.000Z".into(),
            nonce: "nonce".into(),
            mac: String::new(),
        };
        let signed = sign_receipt(receipt, b"test secret").unwrap();
        verify_receipt(&signed, b"test secret").unwrap();
        let mut changed = signed;
        changed.size += 1;
        assert!(matches!(
            verify_receipt(&changed, b"test secret"),
            Err(TicketError::Signature)
        ));
    }
}
