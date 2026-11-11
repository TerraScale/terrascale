use std::net::IpAddr;

use url::{Host, Url};

pub(crate) struct Service {
    host: &'static str,
    regional: bool,
    name: &'static str,
}

pub(crate) const API: Service = Service {
    host: "api.terrascale.tech",
    regional: true,
    name: "the TerraBase API",
};

pub(crate) const AUTH: Service = Service {
    host: "auth.terrascale.tech",
    regional: false,
    name: "ts-auth",
};

fn loopback(url: &Url) -> bool {
    match url.host() {
        Some(Host::Domain(domain)) => domain == "localhost",
        Some(Host::Ipv4(ip)) => IpAddr::V4(ip).is_loopback(),
        Some(Host::Ipv6(ip)) => IpAddr::V6(ip).is_loopback(),
        None => false,
    }
}

fn serves(service: &Service, host: &str) -> bool {
    if host != "terrascale.tech" && !host.ends_with(".terrascale.tech") {
        return true;
    }
    host == service.host
        || host
            .strip_suffix(service.host)
            .and_then(|rest| rest.strip_suffix('.'))
            .is_some_and(|label| service.regional && !label.is_empty() && !label.contains('.'))
}

pub(crate) fn service_origin(
    raw: &str,
    what: &str,
    service: &Service,
) -> std::result::Result<Url, String> {
    let url = Url::parse(raw).map_err(|_| format!("{what} must be an absolute https URL"))?;
    if url.cannot_be_a_base() || url.host().is_none() {
        return Err(format!("{what} must be an absolute https URL"));
    }
    match url.scheme() {
        "https" => {}
        "http" if loopback(&url) => {}
        _ => {
            return Err(format!(
                "{what} must use https; plain http is allowed only for localhost, 127.0.0.0/8 and [::1]"
            ))
        }
    }
    if !url.username().is_empty() || url.password().is_some() {
        return Err(format!("{what} must not carry user info"));
    }
    if url.query().is_some() || url.fragment().is_some() {
        return Err(format!("{what} must not carry a query or a fragment"));
    }
    if url.path() != "/" {
        return Err(format!("{what} must be an origin without a path"));
    }
    if !serves(service, url.host_str().unwrap_or_default()) {
        return Err(format!(
            "{what} names a TerraScale host that does not serve {}",
            service.name
        ));
    }
    Ok(url)
}
