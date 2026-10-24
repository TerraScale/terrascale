#!/usr/bin/env python3
"""User-only OAuth credential handoff. No credential enters argv, disk or output."""
import base64
import getpass
import json
import subprocess
import sys

REGION = "sa-saopaulo-1"
VAULT = "ocid1.vault.oc1.sa-saopaulo-1.ffvnrldqaaexa.abtxeljrlqoda2dl7r527hwngvx5k5j7lhfytswubkmx6rtg5f4mjtv4xzlq"
COMPARTMENT = "ocid1.compartment.oc1..aaaaaaaaypdff62jbchkojfli5i22ybzm3qnptwwsbg3aa2t5ali6te52nzq"
KEY = "ocid1.key.oc1.sa-saopaulo-1.ffvnrldqaaexa.abtxeljr2bdwyvlua7oi6gpg7gl72ln6t2l2fb63tl2cvhllvokc4l2afeeq"


def main():
    if not sys.stdin.isatty():
        sys.exit("Run directly in an interactive terminal; do not pipe credentials.")
    # Collect both before creating anything. getpass disables terminal echo for ids as well as secrets.
    credentials = []
    for provider in ("github", "google"):
        client_id = getpass.getpass(f"{provider} client ID (hidden): ").strip()
        secret = getpass.getpass(f"{provider} client secret (hidden): ").strip()
        if not client_id or not secret or any(c.isspace() for c in client_id + secret):
            sys.exit("Both client IDs and secrets are required, without whitespace; nothing written.")
        credentials.append((provider, {"client_id": client_id, "client_secret": secret}))
    for provider, value in credentials:
        body = {"compartmentId": COMPARTMENT, "vaultId": VAULT, "keyId": KEY,
                "secretName": f"gru-1-ts-auth-{provider}-oauth",
                "secretContentContent": base64.b64encode(json.dumps(value).encode()).decode()}
        # OCI reads the complete request from protected stdin, never command arguments or a temporary file.
        result = subprocess.run(["oci", "vault", "secret", "create-base64", "--region", REGION,
                                 "--from-json", "file:///dev/stdin"], input=json.dumps(body),
                                text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False)
        if result.returncode:
            sys.exit(f"{provider}: Vault create failed; output withheld. Inspect secret metadata before retrying; existing secrets are never overwritten.")
        print(json.loads(result.stdout)["data"]["id"], flush=True)


if __name__ == "__main__":
    main()
