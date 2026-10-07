package auth

import (
	"crypto/aes"
	"crypto/cipher"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json/v2"
	"errors"
	"math"
	"net/http"
	"strconv"
	"time"

	"github.com/a-h/templ"
	"terrascale.tech/dashboard/internal/core"
)

// ts-auth's sign-up endpoint needs the original password to issue another
// verification code. Keep that request in an authenticated, encrypted,
// HttpOnly cookie for at most one hour, and clear it when verification succeeds.
// Using the confidential client's key keeps it stateless across replicas.
type signUpRetry struct {
	Name, Email, Password, Token, ReturnTo string
	Expires, ResendAt                      time.Time
}

func (s signUpRetry) remaining() int { return resendSeconds(time.Until(s.ResendAt)) }

func resendSeconds(wait time.Duration) int { return max(0, int(math.Ceil(wait.Seconds()))) }

func (s signUpRetry) pageData() codePageData {
	return codePageData{
		CodeStep: true, SignUp: true, Name: s.Name, Email: s.Email,
		RedirectTo: s.ReturnTo, ResendSeconds: s.remaining(),
	}
}

func (t *TSAuth) signUpRetryCookie() string { return t.signUpCookie() + "_retry" }

func (t *TSAuth) setSignUpRetryCookie(w http.ResponseWriter, value string, maxAge int) {
	http.SetCookie(w, &http.Cookie{
		Name: t.signUpRetryCookie(), Value: value, Path: "/", MaxAge: maxAge,
		HttpOnly: true, Secure: t.cfg.Secure, SameSite: http.SameSiteLaxMode,
	})
}

func (t *TSAuth) signUpRetryCipher() (cipher.AEAD, error) {
	key := sha256.Sum256([]byte("ts-dashboard/signup-retry/v1\x00" + t.cfg.Issuer + "\x00" + t.cfg.ClientID + "\x00" + t.cfg.ClientSecret))
	block, err := aes.NewCipher(key[:])
	if err != nil {
		return nil, err
	}
	return cipher.NewGCMWithRandomNonce(block)
}

func (t *TSAuth) sealSignUpRetry(retry signUpRetry) (string, error) {
	plain, err := json.Marshal(retry)
	if err != nil {
		return "", err
	}
	aead, err := t.signUpRetryCipher()
	if err != nil {
		return "", err
	}
	sealed := base64.RawURLEncoding.EncodeToString(aead.Seal(nil, nil, plain, []byte(t.signUpRetryCookie())))
	if len(sealed)+len(t.signUpRetryCookie()) > 4000 {
		return "", errors.New("sign-up retry exceeds cookie size")
	}
	return sealed, nil
}

func (t *TSAuth) readSignUpRetry(r *http.Request) (signUpRetry, error) {
	var retry signUpRetry
	cookie, err := r.Cookie(t.signUpRetryCookie())
	if err != nil {
		return retry, err
	}
	aead, err := t.signUpRetryCipher()
	if err != nil {
		return retry, err
	}
	sealed, err := base64.RawURLEncoding.DecodeString(cookie.Value)
	if err != nil {
		return retry, err
	}
	plain, err := aead.Open(nil, nil, sealed, []byte(t.signUpRetryCookie()))
	if err != nil {
		return retry, err
	}
	if err := json.Unmarshal(plain, &retry); err != nil {
		return signUpRetry{}, err
	}
	pending, err := r.Cookie(t.signUpCookie())
	if err != nil || pending.Value != retry.Token || !time.Now().Before(retry.Expires) {
		return signUpRetry{}, errors.New("sign-up retry expired or belongs to another request")
	}
	return retry, nil
}

func (t *TSAuth) resendSignUpCode(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		r.Body = http.MaxBytesReader(w, r.Body, maxLoginBody)
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid resend request", http.StatusBadRequest)
			return
		}
		retry, err := t.readSignUpRetry(r)
		if err != nil {
			t.setSignUpRetryCookie(w, "", -1)
			renderNative(w, r, deps, http.StatusBadRequest, "Verify your email", func(string) templ.Component {
				return signUpRetryExpiredView()
			})
			return
		}
		err = t.SignUp(w, r, deps, RegistrationInput{
			DisplayName: retry.Name, Email: retry.Email, Password: retry.Password,
			ConfirmPassword: retry.Password, AcceptedTerms: true,
		}, retry.ReturnTo)
		if err == nil {
			return
		}
		data, status := retry.pageData(), http.StatusServiceUnavailable
		data.Error = "We could not resend the code. Try again in a moment."
		if input, ok := errors.AsType[InputError](err); ok {
			data.Error, status = input.Message, http.StatusUnprocessableEntity
		}
		if seconds, _ := strconv.Atoi(w.Header().Get("Retry-After")); seconds > 0 {
			data.ResendSeconds, status = seconds, http.StatusTooManyRequests
		} else {
			data.ResendSeconds = max(data.ResendSeconds, 60)
		}
		// Keep the wait after a failed send too: the reserved server budget
		// still applies, including when this page is submitted or revisited.
		retry.ResendAt = time.Now().Add(time.Duration(data.ResendSeconds) * time.Second)
		if sealed, err := t.sealSignUpRetry(retry); err == nil {
			t.setSignUpRetryCookie(w, sealed, max(1, int(time.Until(retry.Expires).Seconds())))
		}
		renderCode(w, r, deps, status, data)
	}
}
