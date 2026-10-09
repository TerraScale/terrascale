.PHONY: prepare build test check
prepare:
	bash prepare-sdk.sh
.sdk/.git/bench-client-prepared: prepare-sdk.sh sdk.rev
	bash prepare-sdk.sh
	touch $@
build test check: .sdk/.git/bench-client-prepared
build:
	bash with-sdk.sh dotnet build src/BenchClient.csproj --configuration Release -p:RestoreLockedMode=true
test:
	bash with-sdk.sh dotnet run --project tests/BenchClient.Tests.csproj --configuration Release -p:RestoreLockedMode=true -- --minimum-expected-tests 79
check:
	bash tests/prepare-sdk.sh
	bash with-sdk.sh dotnet restore tests/BenchClient.Tests.csproj --locked-mode
	bash with-sdk.sh dotnet build tests/BenchClient.Tests.csproj --configuration Release --no-restore
	bash with-sdk.sh dotnet format src/BenchClient.csproj --verify-no-changes --no-restore
	bash with-sdk.sh dotnet format tests/BenchClient.Tests.csproj --verify-no-changes --no-restore
	bash with-sdk.sh dotnet run --project tests/BenchClient.Tests.csproj --configuration Release --no-build --no-restore -- --minimum-expected-tests 79
