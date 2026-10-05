package onboarding

import (
	"context"

	"terrascale.tech/dashboard/internal/core"
)

func mountTools(tools *core.Tools, service Service) {
	core.AddTool(tools, core.ToolSpec{
		Name: "get_onboarding", Title: "Get onboarding", ReadOnly: true,
		Description: "Show your account setup progress: the current step, every step's state, and the paid plans the payment step offers.",
	}, func(ctx context.Context, p core.Principal, _ struct{}) (status, error) {
		return service.Status(ctx, p)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "save_onboarding_step", Title: "Save onboarding step", OpenWorld: true,
		Description: "Complete one account setup step. account-information takes displayName; payment-method takes continueOnFree, " +
			"or plan and interval to start a checkout the user must finish in a browser at checkoutUrl; project takes name and region and finishes setup.",
	}, func(ctx context.Context, p core.Principal, in stepInput) (stepResult, error) {
		return service.SaveStep(ctx, p, in)
	})
}
