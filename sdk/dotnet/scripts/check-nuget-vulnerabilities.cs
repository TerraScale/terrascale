#!/usr/bin/env dotnet run
#:property PublishAot=false
#:include check-nuget-vulnerabilities.lib.cs

return TerraScale.Ci.CheckNugetVulnerabilities.Run(args);
