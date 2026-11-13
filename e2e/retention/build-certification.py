import hashlib
import os
import shutil
import subprocess
from pathlib import Path

repo = Path(__file__).resolve().parents[2]
work = Path(os.environ["E2E_STATE"]).parent / "certification"
source = work / "source"
output = work / "build"
source.mkdir(parents=True, exist_ok=True)
subprocess.run(
    ["rsync", "-a", "--delete", "--exclude", ".git", "--exclude", "node_modules", "--exclude", "bin",
     "--exclude", "obj", str(repo) + "/", str(source) + "/"], check=True
)
native = next(repo.glob("src/vendor/*.Data.Sqlite/native/runtimes"))
shutil.copytree(native, source / native.relative_to(repo), dirs_exist_ok=True)
program = source / "src/TerraBase/Program.cs"
text = program.read_text()
call = "await ApplicationRunCompletion.RunAsync(app);"
assert text.count(call) == 1
replacement = (
    "await app.StartAsync(); "
    "await CertificationProof.RunAsync(app.Services, app.Lifetime.ApplicationStopping); "
    "await app.StopAsync();"
)
program.write_text(text.replace(call, replacement))
shutil.copyfile(repo / "e2e/retention/CertificationProof.cs", source / "src/TerraBase/CertificationProof.cs")
with (work / "build.log").open("w") as log:
    subprocess.run(["dotnet", "build", str(source / "src/TerraBase/TerraBase.csproj"),
                    "-c", "Release", "-o", str(output)], stdout=log, stderr=log, check=True)
receiver_source = work / "receiver-source"
receiver_output = work / "receiver-build"
shutil.rmtree(receiver_source, ignore_errors=True)
receiver_source.mkdir(parents=True)
with subprocess.Popen(
    ["git", "archive", "origin/cos/922cbdaa-C54-delete-undo-receivers"], cwd=repo, stdout=subprocess.PIPE
) as archive:
    subprocess.run(["tar", "-x", "-C", str(receiver_source)], stdin=archive.stdout, check=True)
    assert archive.wait() == 0
shutil.copytree(native, receiver_source / native.relative_to(repo), dirs_exist_ok=True)
with (work / "receiver-build.log").open("w") as log:
    subprocess.run(["dotnet", "build", str(receiver_source / "src/TerraBase/TerraBase.csproj"),
                    "-c", "Release", "-o", str(receiver_output)], stdout=log, stderr=log, check=True)
current = Path(os.environ["E2E_BUILD"]) / "terrabase"
changed = subprocess.check_output(["git", "diff", "--name-only", "origin/main"], cwd=repo, text=True).splitlines()
changed += subprocess.check_output(
    ["git", "log", "--format=", "--name-only", "origin/main..HEAD", "--", "src/TerraBase"],
    cwd=repo, text=True,
).splitlines()
changed += subprocess.check_output(
    ["git", "ls-files", "--others", "--exclude-standard", "src/TerraBase"], cwd=repo, text=True
).splitlines()
inputs = [repo / name for name in sorted(set(changed))
          if name.startswith("src/TerraBase/") and name.endswith(".cs") and (repo / name).is_file()]
helper_stale = [
    file for file in inputs
    if file.name != "Program.cs" and file.read_bytes() != (source / file.relative_to(repo)).read_bytes()
]
if helper_stale:
    for file in helper_stale:
        shutil.copyfile(file, source / file.relative_to(repo))
    with (work / "build-final.log").open("w") as log:
        subprocess.run(["dotnet", "build", str(source / "src/TerraBase/TerraBase.csproj"),
                        "-c", "Release", "-o", str(output)], stdout=log, stderr=log, check=True)
symbols = (current / "terrabase.pdb").read_bytes()
stale = [file for file in inputs if hashlib.sha256(file.read_bytes()).digest() not in symbols]
if stale:
    for file in stale:
        file.touch()
    with (work / "current-build.log").open("w") as log:
        subprocess.run(["bash", str(repo / "e2e/env/build.sh"), str(repo), os.environ["E2E_BUILD"], "terrabase"],
                       cwd=repo, stdout=log, stderr=log, check=True)
symbols = (current / "terrabase.pdb").read_bytes()
assert all(hashlib.sha256(file.read_bytes()).digest() in symbols for file in inputs)
print(output)
