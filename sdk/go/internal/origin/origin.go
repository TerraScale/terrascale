package origin

import (
	"errors"
	"net/url"
	"strconv"
	"strings"
)

type Service struct {
	host     string
	regional bool
	name     string
}

var (
	API    = Service{host: "api.terrascale.tech", regional: true, name: "the TerraBase API"}
	Auth   = Service{host: "auth.terrascale.tech", regional: true, name: "ts-auth"}
	Status = Service{host: "status.terrascale.tech", name: "the status page"}
)

func (s Service) Default() string {
	return "https://" + s.host
}

func Parse(raw string, service Service) (string, error) {
	scheme, host, port, ok := strict(raw)
	if !ok {
		return "", errors.New("must be exactly scheme://host[:port], such as " + service.Default())
	}
	if scheme == "http" && !Loopback(strings.Trim(host, "[]")) {
		return "", errors.New("must use https; plain http is allowed only for localhost, 127.0.0.0/8 and [::1]")
	}
	if !service.serves(host) {
		return "", errors.New("names a TerraScale host that does not serve " + service.name)
	}
	base := scheme + "://" + host + port
	if u, err := url.Parse(base); err != nil || strings.ToLower(u.Host) != host+port {
		return "", errors.New("is read as another host or port by the HTTP client")
	}
	return base, nil
}

func Loopback(host string) bool {
	if strings.ToLower(host) == "localhost" || strings.HasPrefix(host, "127.") && ipv4(host) {
		return true
	}
	groups, ok := ipv6(strings.ToLower(host))
	return ok && groups == [8]uint16{7: 1}
}

func strict(raw string) (string, string, string, bool) {
	for _, c := range []byte(raw) {
		if !('a' <= c && c <= 'z' || 'A' <= c && c <= 'Z' || '0' <= c && c <= '9' || strings.IndexByte("-.:[]/", c) >= 0) {
			return "", "", "", false
		}
	}
	scheme, rest, found := strings.Cut(strings.ToLower(raw), "://")
	rest = strings.TrimSuffix(rest, "/")
	if !found || scheme != "https" && scheme != "http" || strings.Contains(rest, "/") {
		return "", "", "", false
	}
	host, after := rest, ""
	if strings.HasPrefix(rest, "[") {
		end := strings.IndexByte(rest, ']')
		if end < 0 {
			return "", "", "", false
		}
		host, after = rest[:end+1], rest[end+1:]
		if _, ok := ipv6(host[1:end]); !ok {
			return "", "", "", false
		}
	} else if colon := strings.IndexByte(rest, ':'); colon >= 0 {
		host, after = rest[:colon], rest[colon:]
	}
	if !strings.HasPrefix(host, "[") && !name(host) {
		return "", "", "", false
	}
	if after == "" {
		return scheme, host, "", true
	}
	port, err := strconv.Atoi(after[1:])
	if after[0] != ':' || !digits(after[1:]) || len(after) > 6 || after[1] == '0' || err != nil || port > 65535 {
		return "", "", "", false
	}
	if scheme == "https" && port == 443 || scheme == "http" && port == 80 {
		return scheme, host, "", true
	}
	return scheme, host, after, true
}

func name(host string) bool {
	labels := strings.Split(host, ".")
	for _, label := range labels {
		if label == "" || strings.Trim(label, "abcdefghijklmnopqrstuvwxyz0123456789-") != "" {
			return false
		}
	}
	last := labels[len(labels)-1]
	if digits(last) || strings.HasPrefix(last, "0x") && strings.Trim(last[2:], "0123456789abcdef") == "" {
		return ipv4(host)
	}
	return true
}

func ipv4(host string) bool {
	octets := strings.Split(host, ".")
	for _, octet := range octets {
		value, err := strconv.Atoi(octet)
		if !digits(octet) || len(octet) > 3 || len(octet) > 1 && octet[0] == '0' || err != nil || value > 255 {
			return false
		}
	}
	return len(octets) == 4
}

func ipv6(text string) ([8]uint16, bool) {
	var groups [8]uint16
	halves := strings.Split(text, "::")
	if text == "" || len(halves) > 2 {
		return groups, false
	}
	var parts [][]string
	for _, half := range halves {
		if half == "" {
			parts = append(parts, nil)
		} else {
			parts = append(parts, strings.Split(half, ":"))
		}
	}
	count := len(parts[0])
	if len(parts) == 2 {
		count += len(parts[1])
	}
	if len(parts) == 1 && count != 8 || len(parts) == 2 && count > 7 {
		return groups, false
	}
	for i, half := range parts {
		for j, group := range half {
			value, err := strconv.ParseUint(group, 16, 16)
			if len(group) > 4 || strings.Trim(group, "0123456789abcdef") != "" || err != nil {
				return groups, false
			}
			if i == 0 {
				groups[j] = uint16(value)
			} else {
				groups[8-len(half)+j] = uint16(value)
			}
		}
	}
	return groups, true
}

func digits(text string) bool {
	return text != "" && strings.Trim(text, "0123456789") == ""
}

func (s Service) serves(host string) bool {
	if host != "terrascale.tech" && !strings.HasSuffix(host, ".terrascale.tech") {
		return true
	}
	if host == s.host {
		return true
	}
	label, regional := strings.CutSuffix(host, "."+s.host)
	return s.regional && regional && label != "" && !strings.Contains(label, ".")
}
