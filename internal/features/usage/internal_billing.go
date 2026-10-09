package usage

import (
	"context"
	"fmt"
	"math/big"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

// internalBill is a report-only estimate, never a Stripe invoice. Storage is
// estimated at the current logical size for a full month: TS-581 supplies a
// snapshot, not byte-seconds. Reads are metered but unbilled.
type internalBill struct {
	Available        bool      `json:"available"`
	ReportOnly       bool      `json:"reportOnly"`
	PeriodStart      time.Time `json:"periodStart"`
	PeriodEnd        time.Time `json:"periodEnd"`
	Reads            int64     `json:"reads"`
	Writes           int64     `json:"writes"`
	LogicalBytes     int64     `json:"logicalBytes"`
	BaseCents        int64     `json:"baseCents"`
	WriteCents       int64     `json:"writeCents"`
	StorageCents     int64     `json:"storageCents"`
	TotalCents       int64     `json:"totalCents"`
	SpendCapCents    int64     `json:"spendCapCents"`
	CapReached       bool      `json:"capReached"`
	Error            string    `json:"error,omitempty"`
	Alert            string    `json:"alert,omitempty"`
	WriteRateCents   int64     `json:"writeCentsPerMillion"`
	StorageRateCents int64     `json:"storageCentsPerGiBMonth"`
}

func computeInternalBill(plan core.Plan, report usageReport, period core.UsagePeriod) *internalBill {
	bill := &internalBill{
		ReportOnly: true, PeriodStart: period.From, PeriodEnd: period.To,
		BaseCents: plan.PriceCents, SpendCapCents: report.SpendCapCents,
		WriteRateCents: plan.WriteCentsPerMillion, StorageRateCents: plan.StorageCentsPerGiBMonth,
	}
	if plan.ID == "" || len(report.Allowances) != 3 || !report.Allowances[0].Measured || !report.Allowances[1].Measured || !report.Allowances[2].Measured {
		bill.Error = "Every project's monthly usage is required to compute the internal bill and evaluate its cap."
		return bill
	}
	bill.Reads, bill.Writes, bill.LogicalBytes = report.Allowances[0].Used, report.Allowances[1].Used, report.Allowances[2].Used
	writeCents, writeOK := estimateCharge(max(0, bill.Writes-plan.MonthlyOperations()), plan.WriteCentsPerMillion, 1_000_000)
	storageCents, storageOK := estimateCharge(max(0, bill.LogicalBytes-plan.StorageBytes()), plan.StorageCentsPerGiBMonth, 1<<30)
	total := new(big.Int).Add(big.NewInt(plan.PriceCents), big.NewInt(writeCents))
	total.Add(total, big.NewInt(storageCents))
	if !writeOK || !storageOK || plan.PriceCents < 0 || !total.IsInt64() {
		bill.Error = "The internal bill exceeds the supported amount or has invalid reporting rates."
		return bill
	}
	bill.Available, bill.WriteCents, bill.StorageCents, bill.TotalCents = true, writeCents, storageCents, total.Int64()
	bill.CapReached = bill.SpendCapCents > 0 && bill.TotalCents >= bill.SpendCapCents
	if bill.CapReached {
		bill.Alert = fmt.Sprintf("Internal spending cap reached: estimated bill %s, cap %s. Report only; services remain active.", priceUSD(bill.TotalCents), priceUSD(bill.SpendCapCents))
	}
	return bill
}

// estimateCharge rounds each dimension up to a cent without floating-point
// money or overflowing an intermediate multiplication.
func estimateCharge(units, rate, scale int64) (int64, bool) {
	if units < 0 || rate < 0 || scale <= 0 {
		return 0, false
	}
	amount := new(big.Int).Mul(big.NewInt(units), big.NewInt(rate))
	quotient, remainder := new(big.Int), new(big.Int)
	quotient.QuoRem(amount, big.NewInt(scale), remainder)
	if remainder.Sign() > 0 {
		quotient.Add(quotient, big.NewInt(1))
	}
	return quotient.Int64(), quotient.IsInt64()
}

// CheckInternalBilling evaluates usage on demand (for a daily operator job)
// and records one owner-inbox alert per UTC month. It never changes entitlement,
// database state, API keys or Stripe state, including when the cap is reached.
func (s Service) CheckInternalBilling(ctx context.Context, p core.Principal, organizationID, token string) (*internalBill, error) {
	if err := s.deps.Store.AuthorizePlatformAdmin(ctx, p); err != nil {
		return nil, err
	}
	org, err := s.deps.Store.Organization(ctx, organizationID)
	if err != nil {
		return nil, err
	}
	owner, err := s.deps.Store.FindByEmail(ctx, org.Members[org.OwnerUserID].Email)
	if err != nil {
		return nil, err
	}
	ownerPrincipal, err := s.deps.Store.ScopedPrincipal(ctx, owner.Principal(), core.Scope{OrganizationID: org.ID})
	if err != nil {
		return nil, err
	}
	report, err := s.Usage(ctx, ownerPrincipal, token, "", "")
	if err != nil {
		return nil, err
	}
	bill := report.InternalBill
	if bill == nil {
		return nil, core.Invalid("organization has no internal billing policy")
	}
	if !bill.Available {
		return bill, fmt.Errorf("%w: %s", core.ErrUnavailable, bill.Error)
	}
	if !bill.CapReached {
		return bill, nil
	}
	return bill, s.recordInternalCapAlert(ctx, owner, org, bill)
}

func (s Service) recordInternalCapAlert(ctx context.Context, owner core.Account, org core.Organization, bill *internalBill) error {
	month := bill.PeriodStart.UTC().Format("2006-01")
	createdAt, id := time.Now().UTC(), core.NewID()
	return s.deps.Store.Update(ctx, owner.ID, func(account *core.Account) error {
		current, exists := account.Organizations[org.ID]
		if account.User.ID != owner.User.ID || !exists || current.OwnerUserID != owner.User.ID || !current.Billing.Internal || current.Billing.SpendCapCents != bill.SpendCapCents {
			return core.ErrConflict
		}
		for _, item := range account.Notifications {
			if item.OrganizationID == org.ID && item.Type == "internal-cap" && item.BillingMonth == month {
				return nil
			}
		}
		account.Notifications = append(account.Notifications, core.Notification{
			ID: id, OrganizationID: org.ID, Title: "Internal spending cap reached", Body: bill.Alert,
			Severity: "warning", Status: "Unread", Type: "internal-cap", URL: "/organization?tab=billing", CreatedAt: createdAt, BillingMonth: month,
		})
		return nil
	})
}

// NewService exposes the same reporting logic to operator commands.
func NewService(deps core.Deps) Service { return Service{deps: deps} }
