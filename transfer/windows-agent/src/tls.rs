use anyhow::{Context, Result};

pub fn build_nas_client() -> Result<reqwest::Client> {
    let nas_root = reqwest::Certificate::from_pem(include_bytes!(
        "../../../desktop/MuYingDrive.UpdateCore/Trust/nas-root-ca.pem"
    ))
    .context("load embedded NAS CA certificate")?;

    reqwest::Client::builder()
        .tls_built_in_root_certs(false)
        .add_root_certificate(nas_root)
        .connect_timeout(std::time::Duration::from_secs(10))
        .timeout(std::time::Duration::from_secs(120))
        .build()
        .context("build NAS-only HTTP client")
}

#[cfg(test)]
mod tests {
    use super::build_nas_client;
    use reqwest::StatusCode;

    #[tokio::test]
    #[ignore = "requires the NAS and verifies the embedded CA without system roots"]
    async fn embedded_ca_connects_to_api_and_transfer_health() {
        let client = build_nas_client().expect("create app-scoped TLS client");
        let api_url = std::env::var("MUYING_TLS_PROBE_API_URL")
            .expect("set MUYING_TLS_PROBE_API_URL to the NAS API health endpoint");
        let transfer_url = std::env::var("MUYING_TLS_PROBE_TRANSFER_URL")
            .expect("set MUYING_TLS_PROBE_TRANSFER_URL to the NAS transfer health endpoint");

        let api = client.get(api_url).send().await.expect("connect to API using embedded CA");
        assert_eq!(api.status(), StatusCode::OK);

        let transfer = client
            .get(transfer_url)
            .send()
            .await
            .expect("connect to transfer service using embedded CA");
        assert_eq!(transfer.status(), StatusCode::NO_CONTENT);
    }
}
