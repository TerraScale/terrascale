# DSH-207: homologação Stripe no Fly

Estas instruções se referem ao app `terrascale-dashboard-dev`. A integração usa
Stripe.net 53 e a identidade do dashboard fornecida pelos Fly secrets do app.
Não coloque chaves ou segredos em código ou configuração. As regras do fluxo de cobrança e
da auditoria estão em [`stripe.md`](stripe.md).

## 1. Verifique o app dev

No computador de quem administra a organização Fly:

```sh
fly auth login
fly apps list
fly status -a terrascale-dashboard-dev
```

Confirme o app existente e a organização. Se ele não existir, pare; este
runbook não autoriza criar ou substituir infraestrutura. Não use o app de
produção `ts-dashboard` para estes passos.

## 2. Identidade e auditoria somente leitura

Os segredos de runtime de produção são Fly secrets em cada app Fly, incluindo
a chave Stripe e o segredo de assinatura do webhook. Os segredos de CI são
GitHub Actions secrets. O inventário completo fica no repositório terrascale,
em `ops/secrets/`. A aplicação e o comando de auditoria leem essa mesma identidade. O comando não aceita opções,
chaves, segredos, origem, versão da API ou configuração de modo. Ele infere
test/live pelo prefixo da chave entregue e faz apenas leituras pelo Stripe.net
53 `V1` client.

Este runbook não provisiona segredos; veja [`csharp-migration.md`](csharp-migration.md).

Execute o comando no ambiente que contém o checkout do dashboard e a identidade
entregue:

```sh
nice -n 19 dotnet run --project src/TerraScale.Dashboard -- audit-stripe
```

A auditoria usa a origem canônica fixa compartilhada pelo fluxo de Checkout,
retorno do portal e configuração de webhook: `https://dashboard.terrascale.tech`.
Ela não substitui a verificação das tentativas de entrega do endpoint no Stripe
Workbench, nem exercita Checkout, pagamentos, renovações ou alterações de plano.
Nenhum resultado atual de auditoria ou validação ao vivo é afirmado por este
runbook.

## 3. Deploy e readiness

Faça deploy apenas depois de revisar as alterações e confirmar que o app dev
existente está usando a identidade entregue e o backend persistente esperado.
Este repositório não contém `fly.dev.toml`; o primeiro comando depende de um
arquivo de configuração do app dev que ainda não existe. A aplicação C# expõe
readiness em `/readyz`:

```sh
nice -n 19 fly deploy -c fly.dev.toml -a terrascale-dashboard-dev --ha=false --build-arg SOURCE_REVISION=$(git rev-parse HEAD)
fly status -a terrascale-dashboard-dev
fly machine list -a terrascale-dashboard-dev
fly logs -a terrascale-dashboard-dev
curl -fsS https://terrascale-dashboard-dev.fly.dev/readyz
scripts/smoke-native-login.sh https://terrascale-dashboard-dev.fly.dev
```

Não copie credenciais para arquivos dotenv, código ou configuração. Registre
apenas identidade do release e resultados operacionais sem valores secretos.
