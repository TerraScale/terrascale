import base64
import json
import os
import urllib.parse
import urllib.request

from sqlalchemy import create_engine, event


def access_token() -> str:
    """Exchange the API key for a short-lived access token (client credentials grant)."""
    key = os.environ["TERRASCALE_API_KEY"]
    basic = base64.b64encode(f"{key.split('_', 1)[0]}:{key}".encode()).decode()
    request = urllib.request.Request(
        "https://auth.terrascale.tech/terrascale/oauth/token",
        data=urllib.parse.urlencode({"grant_type": "client_credentials"}).encode(),
        headers={"Authorization": f"Basic {basic}"},
    )
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response)["access_token"]


project = os.environ["TERRASCALE_PROJECT_ID"]
database = os.environ["TERRASCALE_DATABASE"]
engine = create_engine(
    f"sqlite+libsql://api.terrascale.tech/v1/projects/{project}/databases/{database}/libsql/?secure=true"
)


@event.listens_for(engine, "do_connect")
def _fresh_token(dialect, conn_rec, cargs, cparams):
    # Access tokens last 15 minutes: every new pooled connection gets a fresh one.
    cparams["auth_token"] = access_token()
