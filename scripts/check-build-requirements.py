"""Validate the active build backend and its extras against the installed lock."""
import re
from importlib import metadata
from pathlib import Path

from packaging.requirements import Requirement
from packaging.utils import canonicalize_name
from packaging.version import Version

try:
    import tomllib
except ModuleNotFoundError:  # Python 3.10 uses the same locked tomli as CI.
    import tomli as tomllib


def main():
    locked = {}
    for raw in Path("requirements-ci.txt").read_text().splitlines():
        line = raw.strip()
        if not line or line.startswith(("#", "--hash=")):
            continue
        match = re.fullmatch(r"([A-Za-z0-9][A-Za-z0-9_.-]*)==([^\s;\\]+)\s*\\?", line)
        if not match:
            raise ValueError(f"Expected a common exact hashed lock pin: {line}")
        name, version = match.groups()
        name = canonicalize_name(name)
        if name in locked:
            raise ValueError(f"Duplicate lock pin: {name}")
        locked[name] = Version(version)

    with Path("pyproject.toml").open("rb") as project:
        requirements = tomllib.load(project)["build-system"]["requires"]
    if not isinstance(requirements, list):
        raise ValueError("build-system.requires must be a list")
    pending = [Requirement(value) for value in requirements]
    checked = set()
    while pending:
        requirement = pending.pop()
        if requirement.marker and not requirement.marker.evaluate():
            continue
        name = canonicalize_name(requirement.name)
        if requirement.url or name not in locked:
            raise ValueError(f"Build requirement has no exact index lock pin: {requirement}")
        distribution = metadata.distribution(name)
        installed = Version(distribution.version)
        if installed != locked[name] or not requirement.specifier.contains(installed, prereleases=True):
            raise ValueError(f"Build requirement {requirement} is incompatible with installed locked {installed}")
        extras = {canonicalize_name(extra) for extra in requirement.extras}
        available = {canonicalize_name(extra) for extra in distribution.metadata.get_all("Provides-Extra", [])}
        if extras - available:
            raise ValueError(f"Unknown build extras for {name}: {sorted(extras - available)}")
        key = (name, tuple(sorted(extras)))
        if key in checked:
            continue
        checked.add(key)
        print(f"Checked build requirement: {requirement} -> {installed}")
        for value in distribution.requires or []:
            dependency = Requirement(value)
            if not dependency.marker or any(dependency.marker.evaluate({"extra": extra}) for extra in extras | {""}):
                dependency.marker = None  # Already evaluated in the owning package's extras context.
                pending.append(dependency)


if __name__ == "__main__":
    try:
        main()
    except (KeyError, OSError, TypeError, ValueError, metadata.PackageNotFoundError) as error:
        raise SystemExit(f"Build requirement check failed: {error}") from error
