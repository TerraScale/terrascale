package origin

import (
	"errors"
	"net"
	"net/url"
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
	base, path, err := Split(raw, service)
	if err != nil {
		return "", err
	}
	if path != "" {
		return "", errors.New("must be an origin without a path")
	}
	return base, nil
}

func Split(raw string, service Service) (string, string, error) {
	u, err := url.Parse(raw)
	if err != nil || u.Host == "" || u.Opaque != "" {
		return "", "", errors.New("must be an absolute https URL")
	}
	host := strings.ToLower(u.Hostname())
	if u.Scheme != "https" && (u.Scheme != "http" || !Loopback(host)) {
		return "", "", errors.New("must use https; plain http is allowed only for localhost, 127.0.0.0/8 and [::1]")
	}
	if u.User != nil {
		return "", "", errors.New("must not carry user info")
	}
	if u.RawQuery != "" || u.ForceQuery || u.Fragment != "" || strings.Contains(raw, "#") {
		return "", "", errors.New("must not carry a query or a fragment")
	}
	if u.RawPath != "" {
		return "", "", errors.New("must not carry an escaped path")
	}
	if !service.serves(host) {
		return "", "", errors.New("names a TerraScale host that does not serve " + service.name)
	}
	if strings.Contains(host, ":") {
		host = "[" + host + "]"
	}
	if port := u.Port(); port != "" && !(u.Scheme == "https" && port == "443") && !(u.Scheme == "http" && port == "80") {
		host += ":" + port
	}
	return u.Scheme + "://" + host, strings.TrimSuffix(u.Path, "/"), nil
}

func Loopback(host string) bool {
	ip := net.ParseIP(host)
	return strings.EqualFold(host, "localhost") || ip != nil && ip.IsLoopback()
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
