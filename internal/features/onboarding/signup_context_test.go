package onboarding

import (
	"net/http"
	"net/http/httptest"
	"net/url"
	"regexp"
	"strings"
	"testing"
)

func TestSignUpFormCarriesPlanAndReturnPath(t *testing.T) {
	t.Parallel()
	response := httptest.NewRecorder()
	newRegistrationMux().ServeHTTP(response, httptest.NewRequest(http.MethodGet, "/onboarding?plan=pro&returnTo=%2Fdatabases%3Ftab%3Dall", nil))

	body := response.Body.String()
	for _, want := range []string{
		`<input type="hidden" name="plan" value="pro">`,
		`<input type="hidden" name="returnTo" value="/databases?tab=all">`,
		`href="/login?returnTo=%2Fdatabases%3Ftab%3Dall"`,
	} {
		if !strings.Contains(body, want) {
			t.Fatalf("sign-up page is missing %s", want)
		}
	}

	response = httptest.NewRecorder()
	newRegistrationMux().ServeHTTP(response, httptest.NewRequest(http.MethodGet, "/onboarding?returnTo=https%3A%2F%2Fevil.example%2F", nil))
	if body := response.Body.String(); strings.Contains(body, `name="returnTo"`) || !strings.Contains(body, `href="/login"`) {
		t.Fatal("sign-up page kept a return path that leaves the dashboard")
	}
}

func TestSignUpContinuesToReturnPathOrChosenPlan(t *testing.T) {
	t.Parallel()
	for _, tc := range []struct {
		name, plan, returnTo, want string
	}{
		{name: "return path", plan: "pro", returnTo: "/access?tab=members", want: "/access?tab=members"},
		{name: "plan", plan: "pro", want: "/onboarding?plan=pro"},
		{name: "outside return path", returnTo: "//evil.example/", want: "/onboarding"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()
			form := registration("correct-horse-battery", "correct-horse-battery")
			form.Set("plan", tc.plan)
			form.Set("returnTo", tc.returnTo)
			response := postRegistration(newRegistrationMux(), form)
			if response.Code != http.StatusSeeOther || response.Header().Get("Location") != tc.want {
				t.Fatalf("sign-up = %d to %q, want %q", response.Code, response.Header().Get("Location"), tc.want)
			}
		})
	}
}

func TestSignUpErrorKeepsPlanAndReturnPath(t *testing.T) {
	t.Parallel()
	form := registration("correct-horse-battery", "different-horse-battery")
	form.Set("plan", "business")
	form.Set("returnTo", "/api-keys")
	body := postRegistration(newRegistrationMux(), form).Body.String()
	if !strings.Contains(body, `name="plan" value="business"`) || !strings.Contains(body, `name="returnTo" value="/api-keys"`) {
		t.Fatal("rejected sign-up dropped the plan or return path")
	}
}

func TestPaymentStepPreselectsTheChosenPlan(t *testing.T) {
	t.Parallel()
	mux := newRegistrationMux()
	registered := postRegistration(mux, registration("correct-horse-battery", "correct-horse-battery"))
	for _, tc := range []struct{ plan, want string }{
		{plan: "pro", want: "pro"},
		{plan: "free", want: "starter"},
		{plan: "", want: "starter"},
	} {
		request := httptest.NewRequest(http.MethodGet, "/onboarding?"+url.Values{"plan": {tc.plan}}.Encode(), nil)
		for _, cookie := range registered.Result().Cookies() {
			request.AddCookie(cookie)
		}
		response := httptest.NewRecorder()
		mux.ServeHTTP(response, request)
		var checked []string
		for _, radio := range regexp.MustCompile(`<input class="ts-onboarding-plan-radio"[^>]*>`).FindAllString(response.Body.String(), -1) {
			if strings.Contains(radio, " checked") {
				checked = append(checked, radio)
			}
		}
		if len(checked) != 1 || !strings.Contains(checked[0], `value="`+tc.want+`"`) {
			t.Fatalf("payment step for plan %q checks %q, want only %q", tc.plan, checked, tc.want)
		}
	}
}
