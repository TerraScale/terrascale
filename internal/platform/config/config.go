package config

import (
	"errors"
	"os"
	"strings"
)

type Config struct {
	Addr           string
	Demo           bool
	Dev            bool
	SecureCookies  bool
	AdminEmail     string
	AdminPassword  string
	SDKBaseURL     string
	SDKAccessToken string
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
	if !c.Demo {
		return Config{}, errors.New("live dashboard persistence is not configured; set TS_DASHBOARD_PRODUCTION_DEMO=1")
	}
	c.SecureCookies = os.Getenv("DASHBOARD_SECURE_COOKIES") == "1" ||
		(os.Getenv("DASHBOARD_SECURE_COOKIES") != "0" && !c.Dev &&
			(strings.HasPrefix(os.Getenv("DASHBOARD_PUBLIC_URL"), "https://") || os.Getenv("FLY_APP_NAME") != ""))
	if (c.AdminEmail == "") != (c.AdminPassword == "") {
		return Config{}, errors.New("demo admin email and password must both be set")
	}
	return c, nil
}
