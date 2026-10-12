# DSH-207: homologação Stripe no Fly

Estas instruções se referem ao app `terrascale-dashboard-dev`. A integração usa
Stripe.net 53 e a identidade do dashboard entregue pelo OCI Vault. Não configure
chaves, segredos, modo ou origem manualmente. As regras do fluxo de cobrança e
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

A produção exige que OCI Vault entregue automaticamente o documento de identidade do dashboard,
incluindo a chave Stripe e o segredo de assinatura do webhook. A aplicação e o
comando de auditoria leem essa mesma identidade. O comando não aceita opções,
chaves, segredos, origem, versão da API ou configuração de modo. Ele infere
test/live pelo prefixo da chave entregue e faz apenas leituras pelo Stripe.net
53 `V1` client.

A entrega automática e o deploy C# ainda precisam de qualificação. Este runbook
não implementa essa entrega; veja [`csharp-migration.md`](csharp-migration.md).

Execute o comando no ambiente que contém o checkout do dashboard e a identidade
entregue:

```sh
nice -n 19 dotnet run --project TerraScale.Dashboard.csproj -- audit-stripe
```

A auditoria usa a origem canônica fixa compartilhada pelo fluxo de Checkout,
retorno do portal e configuração de webhook: `https://dashboard.terrascale.tech`.
Ela não substitui a verificação das tentativas de entrega do endpoint no Stripe
Workbench, nem exercita Checkout, pagamentos, renovações ou alterações de plano.
Nenhum resultado atual de auditoria ou validação ao vivo é afirmado por este
runbook.

## 3. Deploy e readiness

Faça deploy apenas depois de revisar as alterações e confirmar que o app dev
existente está usando a identidade entregue e o backend persistente esperado:

```sh
nice -n 19 fly deploy -c fly.dev.toml -a terrascale-dashboard-dev --ha=false
fly status -a terrascale-dashboard-dev
fly machine list -a terrascale-dashboard-dev
fly logs -a terrascale-dashboard-dev
curl -fsS https://terrascale-dashboard-dev.fly.dev/health/ready
scripts/smoke-native-login.sh https://terrascale-dashboard-dev.fly.dev
```

Não copie credenciais para arquivos dotenv nem importe segredos manualmente no
Fly. Registre apenas identidade do release e resultados operacionais sem valores
secretos.
