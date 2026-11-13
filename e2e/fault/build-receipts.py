import os
import pathlib
import shutil
import subprocess
import sys
import tempfile

repo = pathlib.Path(sys.argv[1]).resolve()
output = pathlib.Path(sys.argv[2]).resolve() / 'receipt-import'
with tempfile.TemporaryDirectory(prefix='journey-receipts-') as directory:
    source = pathlib.Path(directory)
    tracked = subprocess.check_output(
        ['git', 'ls-files', '-z', '--', 'src', 'sdk/dotnet', 'landing-site/static', ':/*.*'], cwd=repo
    ).decode().split('\0')
    for name in filter(None, tracked):
        path = repo / name
        if path.is_file():
            target = source / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(path, target)
    for native in (repo / 'src/vendor').glob('*/native/runtimes'):
        shutil.copytree(native, source / native.relative_to(repo), dirs_exist_ok=True)
    program = source / 'src/TerraBase/Program.cs'
    call = 'await ApplicationRunCompletion.RunAsync(app);'
    original = program.read_text()
    assert original.count(call) == 1
    proof = 'await ReceiptImportProof.RunAsync(app, app.Lifetime.ApplicationStopping);'
    program.write_text(original.replace(call, proof))
    catalog = (repo / 'src/TerraBase/Features/Replicas/RegionalCatalog.cs').read_text()
    start = catalog.index('internal static class RegionalCatalog')
    sending = catalog[:catalog.index('internal sealed record RegionalCell')] + catalog[start:]
    export_receipts = (
        'private static bool Shared(string kind) => '
        'kind.StartsWith("database_purge_receipts/", StringComparison.Ordinal) || '
        'kind.StartsWith("project_closure_receipts/", StringComparison.Ordinal) ||'
    )
    sending = sending.replace('class RegionalCatalog', 'class SendingCatalog').replace(
        'private static bool Shared(string kind) =>', export_receipts
    )
    (source / 'src/TerraBase/Features/Replicas/SendingCatalog.cs').write_text(sending)
    handoff = source / 'src/TerraBase/Features/Replicas/RegionalHandoff.cs'
    handoff.write_text(handoff.read_text().replace('await RegionalCatalog.ExportAsync(', 'await SendingCatalog.ExportAsync('))
    shutil.copyfile(repo / 'e2e/fault/ReceiptImportProof.cs', source / 'src/TerraBase/ReceiptImportProof.cs')
    subprocess.run(
        ['dotnet', 'build', str(source / 'src/TerraBase/TerraBase.csproj'), '-c', 'Release', '-o', str(output)],
        check=True, env=os.environ
    )
