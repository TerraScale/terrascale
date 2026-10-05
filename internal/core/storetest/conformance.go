// Package storetest runs observable core.Store contract checks for every backend.
package storetest

import (
	"terrascale.tech/dashboard/internal/core"
	"testing"
)

// Run requires a fresh, initialized store for each subtest.
func Run(t *testing.T, newStore func(*testing.T) core.Store) {
	t.Helper()
	cases := []struct {
		name string
		run  func(*testing.T, core.Store)
	}{
		{"UpdateRejectsDatabasesPastThePlanLimit", TestUpdateRejectsDatabasesPastThePlanLimit},
		{"UpdateKeepsDatabasesPastTheLimitAfterADowngrade", TestUpdateKeepsDatabasesPastTheLimitAfterADowngrade},
		{"UpdateAccountAsAdminRejectsDatabasesPastThePlanLimit", TestUpdateAccountAsAdminRejectsDatabasesPastThePlanLimit},
		{"AcceptInvitationSelectsDefaultProject", TestAcceptInvitationSelectsDefaultProject},
		{"AcceptInvitationRejectsOrganizationPendingDeletion", TestAcceptInvitationRejectsOrganizationPendingDeletion},
		{"DeletedProjectIsDeselectedForOtherMembers", TestDeletedProjectIsDeselectedForOtherMembers},
		{"AuthorizePlatformAdmin", TestAuthorizePlatformAdmin},
		{"RenameReachesEveryOrganizationOfTheUser", TestRenameReachesEveryOrganizationOfTheUser},
		{"AdminRenameReachesEveryOrganizationOfTheUser", TestAdminRenameReachesEveryOrganizationOfTheUser},
		{"ChangeEmailReachesEveryOrganizationOfTheUser", TestChangeEmailReachesEveryOrganizationOfTheUser},
		{"RegistrationIdentity", TestRegistrationIdentity},
		{"ConcurrentEmailRegistration", TestConcurrentEmailRegistration},
		{"DetachedValuesAndAtomicRollback", TestDetachedValuesAndAtomicRollback},
		{"CredentialGuardRails", TestCredentialGuardRails},
		{"AccessTokenCapAndExpiry", TestAccessTokenCapAndExpiry},
		{"ConcurrentOrganizationDatabaseLimit", TestConcurrentOrganizationDatabaseLimit},
		{"ConcurrentAggregateUpdates", TestConcurrentAggregateUpdates},
		{"ProviderIdentityUniqueness", TestProviderIdentityUniqueness},
		{"PlanCatalogAndExternalReferences", TestPlanCatalogAndExternalReferences},
		{"ScopeGuardAndMembershipRemoval", TestScopeGuardAndMembershipRemoval},
		{"SubjectIdentity", TestSubjectIdentity},
		{"SubjectLinksOnce", TestSubjectLinksOnce},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) { tc.run(t, newStore(t)) })
	}
}
