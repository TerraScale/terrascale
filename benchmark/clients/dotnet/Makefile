.PHONY: prepare build test check
prepare:
	bash prepare-sdk.sh
build:
	dotnet build src/BenchClient.csproj --configuration Release -p:RestoreLockedMode=true
test:
	dotnet run --project tests/BenchClient.Tests.csproj --configuration Release -p:RestoreLockedMode=true -- --minimum-expected-tests 70
check:
	dotnet restore tests/BenchClient.Tests.csproj --locked-mode
	dotnet build tests/BenchClient.Tests.csproj --configuration Release --no-restore
	dotnet format tests/BenchClient.Tests.csproj --verify-no-changes --no-restore
	dotnet run --project tests/BenchClient.Tests.csproj --configuration Release --no-build --no-restore -- --minimum-expected-tests 70
