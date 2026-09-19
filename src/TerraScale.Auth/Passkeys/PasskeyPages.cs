namespace TerraScale.Auth.Passkeys;

using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;

/// <summary>Hosted passkey pages: discoverable sign-in and self-service management,
/// each driving the WebAuthn ceremonies with inline plain JavaScript (no libraries).</summary>
public static class PasskeyPages
{
    /// <summary>The discoverable-credential sign-in page.</summary>
    public static IResult Assert(string slug) => Ui.Page("Sign in with a passkey", $"""
        <h1>Use a passkey</h1>
        <p class="muted">Sign in with a fingerprint, face, screen lock, or security key — no password needed.</p>
        <div id="error" class="error" hidden></div>
        <button id="button" type="button">Continue</button>
        <script>
        {Script(AssertFlow, slug)}
        </script>
        """);

    /// <summary>The passkey management page: list, remove, and register passkeys.</summary>
    public static IResult Manage(string slug, string identifier, IReadOnlyList<Passkey> passkeys)
    {
        var rows = passkeys.Count == 0
            ? """<p class="muted">No passkeys yet.</p>"""
            : string.Join("\n", passkeys.Select(p => Row(slug, p)));
        return Ui.Page("Passkeys", $"""
        <h1>Passkeys</h1>
        <p class="muted">Passkeys for {Ui.Esc(identifier)}.</p>
        <div id="error" class="error" hidden></div>
        <div class="list">
        {rows}
        </div>
        <button id="button" type="button">Register a new passkey</button>
        <script>
        {Script(RegisterFlow, slug)}
        </script>
        """);
    }

    private static string Row(string slug, Passkey passkey) => $"""
        <form class="inline" method="post" action="/ui/{slug}/passkeys/manage/{passkey.Id}/delete">
          <div class="row">
            <span>{Ui.Esc(passkey.Nickname)} <span class="muted">{Ui.Esc(passkey.Algorithm)} · added {FormatDate(passkey.CreatedAt)}</span></span>
            <button class="secondary" type="submit">Remove</button>
          </div>
        </form>
        """;

    private static string FormatDate(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime.ToString("yyyy-MM-dd");

    /// <summary>Splices the instance slug into a flow script, after the shared helpers.</summary>
    private static string Script(string flow, string slug) => (SharedScript + flow).Replace("__SLUG__", slug);

    private const string SharedScript = """
        var b64uEncode=function(bytes){var binary='';for(var i=0;i<bytes.length;i++)binary+=String.fromCharCode(bytes[i]);return btoa(binary).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');};
        var b64uDecode=function(text){var base64=text.replace(/-/g,'+').replace(/_/g,'/');while(base64.length%4)base64+='=';var binary=atob(base64);var bytes=new Uint8Array(binary.length);for(var i=0;i<binary.length;i++)bytes[i]=binary.charCodeAt(i);return bytes;};
        var failWith=function(message){var error=document.getElementById('error');error.hidden=false;error.textContent=message;};
        var postJson=function(url,payload){return fetch(url,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(payload)});};
        var unavailable=function(button){button.disabled=true;failWith('This browser does not support passkeys.');return false;};
        """;

    private const string AssertFlow = """
        (function(){
          var button=document.getElementById('button');
          if(!window.PublicKeyCredential){unavailable(button);return;}
          button.addEventListener('click',function(){
            var error=document.getElementById('error');error.hidden=true;
            fetch('/ui/__SLUG__/passkeys/assert/options',{method:'POST'})
              .then(function(response){if(!response.ok)throw new Error('Could not start passkey sign-in.');return response.json();})
              .then(function(options){
                return navigator.credentials.get({publicKey:{
                  challenge:b64uDecode(options.publicKey.challenge),
                  rpId:options.publicKey.rpId,
                  allowCredentials:[],
                  userVerification:'preferred',
                  timeout:options.publicKey.timeout
                }});
              })
              .then(function(credential){
                var response=credential.response;
                return postJson('/ui/__SLUG__/passkeys/assert/verify',{
                  id:credential.id,
                  response:{
                    clientDataJSON:b64uEncode(new Uint8Array(response.clientDataJSON)),
                    authenticatorData:b64uEncode(new Uint8Array(response.authenticatorData)),
                    signature:b64uEncode(new Uint8Array(response.signature)),
                    userHandle:response.userHandle?b64uEncode(new Uint8Array(response.userHandle)):null
                  }
                });
              })
              .then(function(response){
                return response.json().then(function(body){
                  if(!response.ok)throw new Error(body&&body.error&&body.error.message||'Sign-in failed.');
                  window.location.href=body.redirect;
                });
              })
              .catch(function(error){failWith(error&&error.message?error.message:'Sign-in failed.');});
          });
        })();
        """;

    private const string RegisterFlow = """
        (function(){
          var button=document.getElementById('button');
          if(!window.PublicKeyCredential){unavailable(button);return;}
          button.addEventListener('click',function(){
            var error=document.getElementById('error');error.hidden=true;
            fetch('/ui/__SLUG__/passkeys/register/options',{method:'POST'})
              .then(function(response){if(!response.ok)throw new Error('Could not start registration.');return response.json();})
              .then(function(options){
                var pk=options.publicKey;
                return navigator.credentials.create({publicKey:{
                  challenge:b64uDecode(pk.challenge),
                  rp:pk.rp,
                  user:{id:b64uDecode(pk.user.id),name:pk.user.name,displayName:pk.user.displayName},
                  pubKeyCredParams:pk.pubKeyCredParams,
                  excludeCredentials:(pk.excludeCredentials||[]).map(function(c){return{type:c.type,id:b64uDecode(c.id)};}),
                  authenticatorSelection:pk.authenticatorSelection,
                  timeout:pk.timeout,
                  attestation:'none'
                }});
              })
              .then(function(credential){
                var response=credential.response;
                return postJson('/ui/__SLUG__/passkeys/register',{
                  id:credential.id,
                  nickname:credential.authenticatorAttachment==='platform'?'This device':'Security key',
                  response:{
                    clientDataJSON:b64uEncode(new Uint8Array(response.clientDataJSON)),
                    attestationObject:b64uEncode(new Uint8Array(response.attestationObject)),
                    transports:[]
                  }
                });
              })
              .then(function(response){
                return response.json().then(function(body){
                  if(!response.ok)throw new Error(body&&body.error&&body.error.message||'Registration failed.');
                  window.location.reload();
                });
              })
              .catch(function(error){failWith(error&&error.message?error.message:'Registration failed.');});
          });
        })();
        """;
}
