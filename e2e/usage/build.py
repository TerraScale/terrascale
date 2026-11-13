import pathlib
import subprocess
import sys
import tempfile
from xml.sax.saxutils import escape

root = pathlib.Path(sys.argv[1]).resolve()
output = pathlib.Path(sys.argv[2]).resolve()
with tempfile.TemporaryDirectory(prefix="usage-journey-") as directory:
    temporary = pathlib.Path(directory)
    program = (root / "src/TerraBase/Program.cs").read_text()
    clock = "builder.Services.AddSingleton(TimeProvider.System);"
    run = "await ApplicationRunCompletion.RunAsync(app);"
    assert program.count(clock) == 1 and program.count(run) == 1
    program = program.replace(clock, "builder.Services.AddSingleton<TimeProvider>(new UsageJourneyClock());")
    program = program.replace(run, "await app.StartAsync(); "
        "await CompleteMonths.RunAsync(app.Services, app.Lifetime.ApplicationStopping); await app.StopAsync();")
    (temporary / "Program.cs").write_text(program)
    targets = temporary / "usage.targets"
    targets.write_text(
        '<Project><Target Name="UsageJourneySources" BeforeTargets="CoreCompile" '
        'Condition="&apos;$(MSBuildProjectName)&apos; == &apos;TerraBase&apos;"><ItemGroup>'
        '<Compile Remove="Program.cs" />'
        f'<Compile Include="{escape(str(temporary / "Program.cs"))}" />'
        f'<Compile Include="{escape(str(root / "e2e/usage/CompleteMonths.cs"))}" />'
        '</ItemGroup></Target></Project>'
    )
    subprocess.run([
        "dotnet", "build", str(root / "src/TerraBase/TerraBase.csproj"), "-c", "Release", "--no-restore",
        "--output", str(output), f"-p:CustomBeforeMicrosoftCommonTargets={targets}",
    ], check=True)
