# Function and server import JSON

Function deploy returns SDK metadata, list/logs return arrays and delete returns
`{name,deleted:true}`. Invoke returns `{body:<JSON value>}` when its response is valid
JSON; arbitrary response bytes use `{bodyBase64:<base64>}`; no response uses `{body:null}`.
Non-success handler responses produce only the common safe error envelope and exit 1.

Server import returns its final typed ImportJob on success. Preview/progress output
uses stderr in JSON mode; consent and blocking checks still precede StartImport.
An unsuccessful import can leave server-side work; inspect `import status` before retrying.
