use url::{Host, Url};

pub(crate) struct Service {
    host: &'static str,
    name: &'static str,
}

pub(crate) const API: Service = Service {
    host: "api.terrascale.tech",
    name: "the TerraBase API",
};

pub(crate) const AUTH: Service = Service {
    host: "auth.terrascale.tech",
    name: "ts-auth",
};

fn digits(text: &str) -> bool {
    !text.is_empty() && text.bytes().all(|c| c.is_ascii_digit())
}

fn ipv4(host: &str) -> bool {
    let octets: Vec<&str> = host.split('.').collect();
    octets.len() == 4
        && octets.iter().all(|octet| {
            digits(octet)
                && octet.len() <= 3
                && !(octet.len() > 1 && octet.starts_with('0'))
                && octet.parse::<u16>().is_ok_and(|value| value <= 255)
        })
}

fn ipv6(text: &str) -> Option<[u16; 8]> {
    let halves: Vec<&str> = text.split("::").collect();
    if text.is_empty() || halves.len() > 2 {
        return None;
    }
    let parts: Vec<Vec<&str>> = halves
        .iter()
        .map(|half| {
            if half.is_empty() {
                Vec::new()
            } else {
                half.split(':').collect()
            }
        })
        .collect();
    let count: usize = parts.iter().map(Vec::len).sum();
    if parts.len() == 1 && count != 8 || parts.len() == 2 && count > 7 {
        return None;
    }
    let mut groups = [0u16; 8];
    for (index, half) in parts.iter().enumerate() {
        for (position, group) in half.iter().enumerate() {
            if group.is_empty() || group.len() > 4 || !group.bytes().all(|c| c.is_ascii_hexdigit())
            {
                return None;
            }
            let slot = if index == 0 {
                position
            } else {
                8 - half.len() + position
            };
            groups[slot] = u16::from_str_radix(group, 16).ok()?;
        }
    }
    Some(groups)
}

fn bracketed(host: &str) -> Option<[u16; 8]> {
    host.strip_prefix('[')
        .and_then(|rest| rest.strip_suffix(']'))
        .and_then(ipv6)
}

fn name(host: &str) -> bool {
    let labels: Vec<&str> = host.split('.').collect();
    if !labels.iter().all(|label| {
        !label.is_empty()
            && label
                .bytes()
                .all(|c| c.is_ascii_lowercase() || c.is_ascii_digit() || c == b'-')
    }) {
        return false;
    }
    let last = labels[labels.len() - 1];
    let numeric = digits(last)
        || last
            .strip_prefix("0x")
            .is_some_and(|hex| hex.bytes().all(|c| c.is_ascii_hexdigit()));
    !numeric || ipv4(host)
}

fn loopback(host: &str) -> bool {
    host == "localhost"
        || host.starts_with("127.") && ipv4(host)
        || bracketed(host) == Some([0, 0, 0, 0, 0, 0, 0, 1])
}

fn strict(raw: &str) -> Option<(String, String, Option<u16>)> {
    if !raw
        .bytes()
        .all(|c| c.is_ascii_alphanumeric() || b"-.:[]/".contains(&c))
    {
        return None;
    }
    let lower = raw.to_ascii_lowercase();
    let (scheme, rest) = lower.split_once("://")?;
    let rest = rest.strip_suffix('/').unwrap_or(rest);
    if scheme != "https" && scheme != "http" || rest.contains('/') {
        return None;
    }
    let (host, after) = if rest.starts_with('[') {
        let end = rest.find(']')?;
        ipv6(&rest[1..end])?;
        rest.split_at(end + 1)
    } else {
        rest.split_at(rest.find(':').unwrap_or(rest.len()))
    };
    if !host.starts_with('[') && !name(host) {
        return None;
    }
    let port = match after.strip_prefix(':') {
        None if after.is_empty() => None,
        Some(port) if digits(port) && port.len() <= 5 && !port.starts_with('0') => {
            Some(port.parse::<u16>().ok()?)
        }
        _ => return None,
    };
    let port =
        port.filter(|&port| !(scheme == "https" && port == 443 || scheme == "http" && port == 80));
    Some((scheme.to_owned(), host.to_owned(), port))
}

fn serves(service: &Service, host: &str) -> bool {
    if host != "terrascale.tech" && !host.ends_with(".terrascale.tech") {
        return true;
    }
    host == service.host
        || host
            .strip_suffix(service.host)
            .and_then(|rest| rest.strip_suffix('.'))
            .is_some_and(|label| !label.is_empty() && !label.contains('.'))
}

fn same_host(parsed: Option<Host<&str>>, host: &str) -> bool {
    match parsed {
        Some(Host::Domain(domain)) => domain == host,
        Some(Host::Ipv4(address)) => address.to_string() == host,
        Some(Host::Ipv6(address)) => bracketed(host) == Some(address.segments()),
        None => false,
    }
}

pub(crate) fn service_origin(
    raw: &str,
    what: &str,
    service: &Service,
) -> std::result::Result<Url, String> {
    let (scheme, host, port) = strict(raw).ok_or_else(|| {
        format!(
            "{what} must be exactly scheme://host[:port], such as https://{}",
            service.host
        )
    })?;
    if scheme == "http" && !loopback(&host) {
        return Err(format!(
            "{what} must use https; plain http is allowed only for localhost, 127.0.0.0/8 and [::1]"
        ));
    }
    if !serves(service, &host) {
        return Err(format!(
            "{what} names a TerraScale host that does not serve {}",
            service.name
        ));
    }
    let origin = match port {
        Some(port) => format!("{scheme}://{host}:{port}"),
        None => format!("{scheme}://{host}"),
    };
    Url::parse(&origin)
        .ok()
        .filter(|url| url.port() == port && same_host(url.host(), &host))
        .ok_or_else(|| format!("{what} is read as another host or port by the HTTP client"))
}
