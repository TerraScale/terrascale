use base64::{Engine, engine::general_purpose::STANDARD};
use std::{collections::HashMap, io};

#[derive(Debug, Clone)]
pub struct AuthOptions {
    pub listen: String,
    pub public_origin: String,
    pub terrascale_api_url: String,
    pub terrascale_api_key: String,
    pub terrascale_project_id: Option<String>,
    pub terrascale_tenant_id: Option<String>,
    pub database: String,
    pub master_key: Vec<u8>,
    pub audit_signing_key: Option<Vec<u8>>,
    pub audit_verification_keys: Vec<Vec<u8>>,
    pub operator_key: String,
    pub metrics_key: Option<String>,
    pub rate_limit_per_window: u32,
    pub stripe_secret_key: Option<String>,
    pub stripe_webhook_secret: Option<String>,
    pub stripe_api_url: String,
    pub price_pro: Option<String>,
    pub price_enterprise: Option<String>,
    pub smtp_host: Option<String>,
    pub smtp_port: u16,
    pub smtp_username: Option<String>,
    pub smtp_password: Option<String>,
    pub smtp_from_address: Option<String>,
}

const KEYS: &[&str] = &[
    "listen",
    "public-origin",
    "terrascale-api-url",
    "terrascale-api-key",
    "terrascale-project-id",
    "terrascale-tenant-id",
    "database",
    "master-key",
    "audit-signing-key",
    "audit-verify-keys",
    "operator-key",
    "metrics-key",
    "rate-limit",
    "stripe-secret-key",
    "stripe-webhook-secret",
    "stripe-api-url",
    "price-pro",
    "price-enterprise",
    "smtp-host",
    "smtp-port",
    "smtp-username",
    "smtp-password",
    "smtp-from-address",
];

impl AuthOptions {
    pub fn load(args: impl IntoIterator<Item = String>) -> io::Result<Self> {
        let mut values = HashMap::new();
        for (key, value) in std::env::vars() {
            if let Some(name) = key.strip_prefix("TS_AUTH_") {
                values.insert(name.to_ascii_lowercase().replace('_', "-"), value);
            }
        }
        for arg in args {
            if !arg.starts_with("--") {
                continue;
            }
            let (key, value) = arg[2..].split_once('=').ok_or_else(|| {
                invalid(format!(
                    "Unknown or malformed option '{arg}' (expected --key=value)"
                ))
            })?;
            if !KEYS.contains(&key) {
                return Err(invalid(format!(
                    "Unknown or malformed option '{arg}' (expected --key=value)"
                )));
            }
            values.insert(key.to_owned(), value.to_owned());
        }
        let required = |key: &str| -> io::Result<String> {
            values
                .get(key)
                .filter(|value| !value.trim().is_empty())
                .cloned()
                .ok_or_else(|| {
                    invalid(format!(
                        "TS_AUTH_{} is required",
                        key.replace('-', "_").to_ascii_uppercase()
                    ))
                })
        };
        let optional = |key: &str| -> Option<String> {
            values
                .get(key)
                .filter(|value| !value.trim().is_empty())
                .cloned()
        };
        let decode_key = |key: &str, raw: &str| -> io::Result<Vec<u8>> {
            let bytes = STANDARD.decode(raw).map_err(|_| {
                invalid(format!(
                    "TS_AUTH_{} must be base64",
                    key.replace('-', "_").to_ascii_uppercase()
                ))
            })?;
            if bytes.len() != 32 {
                return Err(invalid(format!(
                    "TS_AUTH_{} must decode to 32 bytes",
                    key.replace('-', "_").to_ascii_uppercase()
                )));
            }
            Ok(bytes)
        };
        let public_origin = required("public-origin")?;
        let terrascale_api_url =
            optional("terrascale-api-url").unwrap_or_else(|| "https://api.terrascale.tech".into());
        let stripe_api_url =
            optional("stripe-api-url").unwrap_or_else(|| "https://api.stripe.com".into());
        for (key, raw) in [
            ("PUBLIC_ORIGIN", &public_origin),
            ("TERRASCALE_API_URL", &terrascale_api_url),
            ("STRIPE_API_URL", &stripe_api_url),
        ] {
            let url = reqwest::Url::parse(raw)
                .map_err(|_| invalid(format!("TS_AUTH_{key} must be an absolute HTTP(S) URL")))?;
            if !matches!(url.scheme(), "http" | "https")
                || url.host_str().is_none()
                || !url.username().is_empty()
                || url.password().is_some()
                || url.query().is_some()
                || url.fragment().is_some()
            {
                return Err(invalid(format!(
                    "TS_AUTH_{key} must be an absolute HTTP(S) URL"
                )));
            }
        }
        let audit_signing_key = optional("audit-signing-key")
            .map(|raw| decode_key("audit-signing-key", &raw))
            .transpose()?;
        let verify_raw = optional("audit-verify-keys").unwrap_or_default();
        let verify_values: Vec<_> = verify_raw
            .split(';')
            .map(str::trim)
            .filter(|value| !value.is_empty())
            .collect();
        if verify_values.len() > 5 {
            return Err(invalid(
                "TS_AUTH_AUDIT_VERIFY_KEYS may contain at most 5 previous keys",
            ));
        }
        let audit_verification_keys = verify_values
            .into_iter()
            .map(|raw| decode_key("audit-verify-keys", raw))
            .collect::<io::Result<Vec<_>>>()?;
        let smtp_parts = [
            optional("smtp-host"),
            optional("smtp-username"),
            optional("smtp-password"),
            optional("smtp-from-address"),
        ];
        if smtp_parts.iter().any(Option::is_some) && smtp_parts.iter().any(Option::is_none) {
            return Err(invalid("TS_AUTH_SMTP_* is partially configured"));
        }
        let smtp_port = optional("smtp-port")
            .map(|raw| {
                raw.parse::<u16>()
                    .map_err(|_| invalid("TS_AUTH_SMTP_PORT must be between 1 and 65535"))
            })
            .transpose()?
            .unwrap_or(587);
        if smtp_port == 0 {
            return Err(invalid("TS_AUTH_SMTP_PORT must be between 1 and 65535"));
        }
        let rate_limit_per_window = optional("rate-limit")
            .map(|raw| {
                raw.parse::<u32>()
                    .map_err(|_| invalid("TS_AUTH_RATE_LIMIT must be positive"))
            })
            .transpose()?
            .unwrap_or(1000);
        if rate_limit_per_window == 0 {
            return Err(invalid("TS_AUTH_RATE_LIMIT must be positive"));
        }
        Ok(Self {
            listen: optional("listen").unwrap_or_else(|| "http://0.0.0.0:8080".into()),
            public_origin,
            terrascale_api_url,
            terrascale_api_key: required("terrascale-api-key")?,
            terrascale_project_id: optional("terrascale-project-id"),
            terrascale_tenant_id: optional("terrascale-tenant-id"),
            database: required("database")?,
            master_key: decode_key("master-key", &required("master-key")?)?,
            audit_signing_key,
            audit_verification_keys,
            operator_key: required("operator-key")?,
            metrics_key: optional("metrics-key"),
            rate_limit_per_window,
            stripe_secret_key: optional("stripe-secret-key"),
            stripe_webhook_secret: optional("stripe-webhook-secret"),
            stripe_api_url,
            price_pro: optional("price-pro"),
            price_enterprise: optional("price-enterprise"),
            smtp_host: smtp_parts[0].clone(),
            smtp_port,
            smtp_username: smtp_parts[1].clone(),
            smtp_password: smtp_parts[2].clone(),
            smtp_from_address: smtp_parts[3].clone(),
        })
    }
}

fn invalid(message: impl Into<String>) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidInput, message.into())
}
