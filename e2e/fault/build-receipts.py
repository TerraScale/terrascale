import os
import pathlib
import shutil
import subprocess
import sys
import tempfile

repo = pathlib.Path(sys.argv[1]).resolve()
output = pathlib.Path(sys.argv[2]).resolve() / 'catalog-import'
natives = repo / 'native/runtimes'
assert natives.is_dir(), f'{natives} is missing; run native/build-native.sh first'
with tempfile.TemporaryDirectory(prefix='journey-catalog-') as directory:
    source = pathlib.Path(directory)
    files = subprocess.check_output(
        ['git', 'ls-files', '-z', '--cached', '--others', '--exclude-standard', '--', 'src', 'sdk/dotnet', ':(glob)*'], cwd=repo
    ).decode().split('\0')
    for name in filter(None, files):
        path = repo / name
        if path.is_file():
            target = source / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(path, target)
    shutil.copytree(natives, source / 'native/runtimes')
    program = source / 'src/TerraBase/Program.cs'
    call = 'await ApplicationRunCompletion.RunAsync(app);'
    original = program.read_text()
    assert original.count(call) == 1
    program.write_text(original.replace(call, 'await CatalogImportProof.RunAsync(app, app.Lifetime.ApplicationStopping);'))
    shutil.copyfile(repo / 'e2e/fault/CatalogImportProof.cs', source / 'src/TerraBase/CatalogImportProof.cs')
    subprocess.run(
        ['dotnet', 'build', str(source / 'src/TerraBase/TerraBase.csproj'), '-c', 'Release', '-o', str(output)],
        check=True, env=os.environ
    )
