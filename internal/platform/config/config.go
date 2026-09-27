package config

import (
	"errors"
	"os"
	"strings"
)

// defaultStatusURL is the public status page summary behind the shell's
// incident and maintenance banner.
const defaultStatusURL = "https://status.terrascale.tech/api/v2/summary.json"

type Config struct {
	Addr           string
	Demo           bool
	Dev            bool
	SecureCookies  bool
	AdminEmail     string
	AdminPassword  string
	SDKBaseURL     string
	SDKAccessToken string
	// StatusURL is the Statuspage-compatible summary.json the shell banner
	// reads; empty disables the banner.
	StatusURL string
	// ClientIPHeader names the header in which the reverse proxy in front of
	// the dashboard reports the client address; empty means the connection's
	// remote address is the client's.
	ClientIPHeader string
}

func Load() (Config, error) {
	c := Config{
		Addr:           strings.TrimSpace(os.Getenv("DASHBOARD_ADDR")),
		Demo:           os.Getenv("TS_DASHBOARD_PRODUCTION_DEMO") == "1",
		Dev:            os.Getenv("DASHBOARD_DEV") == "1",
		AdminEmail:     strings.TrimSpace(os.Getenv("DASHBOARD_DEMO_ADMIN_EMAIL")),
		AdminPassword:  os.Getenv("DASHBOARD_DEMO_ADMIN_PASSWORD"),
		SDKBaseURL:     strings.TrimSpace(os.Getenv("TS_API_BASE_URL")),
		SDKAccessToken: os.Getenv("TS_API_ACCESS_TOKEN"),
	}
	if c.Addr == "" {
		c.Addr = ":8080"
	}
	// Set DASHBOARD_STATUS_SUMMARY_URL to another summary, or to an empty
	// value to disable the status banner.
	c.StatusURL = defaultStatusURL
	if statusURL, set := os.LookupEnv("DASHBOARD_STATUS_SUMMARY_URL"); set {
		c.StatusURL = strings.TrimSpace(statusURL)
	}
	if !c.Demo {
		return Config{}, errors.New("live dashboard persistence is not configured; set TS_DASHBOARD_PRODUCTION_DEMO=1")
	}
	c.SecureCookies = os.Getenv("DASHBOARD_SECURE_COOKIES") == "1" ||
		(os.Getenv("DASHBOARD_SECURE_COOKIES") != "0" && !c.Dev &&
			(strings.HasPrefix(os.Getenv("DASHBOARD_PUBLIC_URL"), "https://") || os.Getenv("FLY_APP_NAME") != ""))
	// On Fly.io every request arrives through Fly Proxy, which reports the
	// client address in Fly-Client-IP.
	if os.Getenv("FLY_APP_NAME") != "" {
		c.ClientIPHeader = "Fly-Client-IP"
	}
	if (c.AdminEmail == "") != (c.AdminPassword == "") {
		return Config{}, errors.New("demo admin email and password must both be set")
	}
	return c, nil
}
