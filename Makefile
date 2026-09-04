.PHONY: check

check:
	dotnet format TerraScale.DotNet.slnx --verify-no-changes
	dotnet build TerraScale.DotNet.slnx --configuration Release
	dotnet test TerraScale.DotNet.slnx --configuration Release --no-build --no-progress
