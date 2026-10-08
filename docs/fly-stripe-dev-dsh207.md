# DSH-207: homologação Stripe no Fly

Estas instruções são para `terrascale-dashboard-dev` e chaves **Stripe de teste**.
A referência de configuração e o comportamento da integração estão em
[`stripe.md`](stripe.md).
O runtime atual usa TerraBase via ts-go para contas, sessões, throttle e estado
Stripe, e a API headless do ts-auth para autenticação. O login permanece nas
páginas do dashboard; apenas SSO redireciona para a página hospedada. Execuções
locais de demonstração continuam com `TS_DASHBOARD_PRODUCTION_DEMO=1`.

## 1. Acesso e app

No computador de quem administra a organização Fly:

```sh
fly auth login
fly apps list
fly status -a terrascale-dashboard-dev
```

Confirme o app existente e a organização. Se ele não existir, pare: estas
instruções não autorizam criar ou substituir infraestrutura. Não use o app de
produção `ts-dashboard` para estes testes.

## 2. Configuração dev persistente

Crie um `fly.dev.toml` próprio a partir de `fly.toml` e revise pelo menos:

```toml
app = "terrascale-dashboard-dev"
primary_region = "gru"

[env]
  DASHBOARD_ADDR = ":8080"
  STRIPE_MODE = "test"
  DASHBOARD_PUBLIC_ORIGIN = "https://terrascale-dashboard-dev.fly.dev"
  DASHBOARD_PUBLIC_URL = "https://terrascale-dashboard-dev.fly.dev"
  DASHBOARD_SECURE_COOKIES = "1"
```

Remova `TS_DASHBOARD_PRODUCTION_DEMO` desse arquivo. Com o modo demo
desligado, o app valida todas as variáveis Stripe na inicialização e não sobe
se alguma estiver ausente ou inconsistente. Mantenha o serviço HTTP na porta interna 8080, HTTPS obrigatório e o health
check `/health/ready`. Preserve a conexão existente `TERRABASE_URL`,
`TERRABASE_API_KEY`, `TERRABASE_PROJECT_ID` e `TERRABASE_DATABASE`. Configure
`TS_AUTH_ISSUER`, `TS_AUTH_CLIENT_ID`, `TS_AUTH_CLIENT_SECRET` e
`TS_AUTH_REDIRECT_URL` para esse app, e `TS_DASHBOARD_CSRF_SECRET` com pelo menos
32 bytes. Valide o arquivo antes do deploy:

```sh
fly config validate --strict -c fly.dev.toml -a terrascale-dashboard-dev
```

## 3. Stripe e segredos

No Stripe Workbench em modo teste, crie um endpoint exclusivo para
`https://terrascale-dashboard-dev.fly.dev/api/auth/stripe/webhook` na versão de
API do stripe-go (`stripe.APIVersion`, hoje `2026-08-26.dahlia`) com todos os
eventos listados em [`stripe.md`](stripe.md#webhook-endpoint). Em Settings →
Checkout, habilite "Limit customers to one subscription".

Anote o segredo de assinatura **desse endpoint**, os seis IDs de preço de
teste e uma chave restrita (`rk_test_…`) ou secreta de teste em um arquivo privado `KEY=VALUE`, fora do
repositório. Use os nomes `STRIPE_SECRET_KEY`, `STRIPE_WEBHOOK_SECRET`,
`STRIPE_PRICE_STARTER_MONTHLY`, `STRIPE_PRICE_STARTER_ANNUAL`,
`STRIPE_PRICE_PRO_MONTHLY`, `STRIPE_PRICE_PRO_ANNUAL`,
`STRIPE_PRICE_BUSINESS_MONTHLY` e `STRIPE_PRICE_BUSINESS_ANNUAL`, além de
`STRIPE_MODE=test` e `DASHBOARD_PUBLIC_ORIGIN`. O estado Stripe usa o mesmo
banco do dashboard: preserve as credenciais `TERRABASE_*`, `TS_AUTH_*` e CSRF
existentes, sem configurar um escopo `TS_BILLING_*` separado.
As permissões da chave restrita estão em
[`stripe.md`](stripe.md).
Importe sem colocar valores no histórico do shell:

```sh
fly secrets import --stage -a terrascale-dashboard-dev < /caminho/privado/stripe-dev.env
fly secrets list -a terrascale-dashboard-dev
```

No portal do Stripe em modo teste, habilite atualização do cartão, histórico
de faturas e cancelamento. A alteração de plano deve continuar desabilitada
no portal porque o dashboard confirma essa ação.

## 4. Deploy e verificação

Depois de verificar a configuração persistente do app dev existente:

```sh
fly deploy -c fly.dev.toml -a terrascale-dashboard-dev --ha=false
fly status -a terrascale-dashboard-dev
fly machine list -a terrascale-dashboard-dev
fly logs -a terrascale-dashboard-dev
curl -fsS https://terrascale-dashboard-dev.fly.dev/health/ready
nice -n 19 python3 scripts/audit-stripe.py --online --env-file /caminho/privado/stripe-dev.env
scripts/smoke-native-login.sh https://terrascale-dashboard-dev.fly.dev
```

O arquivo privado usado na auditoria também precisa conter
`DASHBOARD_PUBLIC_ORIGIN=https://terrascale-dashboard-dev.fly.dev` e
`STRIPE_MODE=test`. O script não imprime chaves nem IDs de clientes, mas
retorna falha se algum preço, produto, evento ou endpoint não corresponder.

No Workbench, confira as tentativas do endpoint dev e exija respostas 2xx.
O inventário atual mostrou dez eventos `invoice.created` com uma entrega
pendente. Resolva essas entregas e confirme a finalização das faturas antes
de aprovar renovação e fim de trial. Faça o percurso de Checkout, upgrade,
downgrade, troca de periodicidade, volta ao Free, cancelamento e falha de
pagamento usando cartões e relógios de teste. Reinicie o app dev após uma
alteração e confira que conta, vínculo Stripe e plano continuam presentes.
