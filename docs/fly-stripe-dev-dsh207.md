# DSH-207: homologação Stripe no Fly

Estas instruções são para `terrascale-dashboard-dev` e chaves **Stripe de teste**.
A referência de configuração e o comportamento da integração estão em
[`stripe.md`](stripe.md).
O runtime atual usa TerraBase via ts-go para contas, sessões, throttle e estado
Stripe, e a API headless do ts-auth para autenticação. O login permanece nas
páginas do dashboard; apenas SSO redireciona para a página hospedada. Fakes
e demonstrações ficam restritos a binários de teste isolados.

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

## 2. Contrato de homologação e segredos

O [contrato de configuração zero](zero-configuration.md) exige persistência e
autenticação reais no app compartilhado. Fakes e demonstrações pertencem somente
a código e binários de teste. Os seletores de modo atuais são pendências do
runtime; estas instruções não autorizam usá-los para ativar recursos.

OCI Vault deve entregar automaticamente credenciais de serviço, CSRF e chaves
Stripe armazenadas uma única vez. O provisionamento deve vincular o app dev ao
issuer, callbacks, banco e conta Stripe de teste corretos. Não copie credenciais
para arquivos dotenv ou importe segredos manualmente no Fly.

Preserve HTTPS, cookies seguros, readiness, isolamento da conta de teste e
permissões mínimas. Verifique o endpoint exclusivo
`https://terrascale-dashboard-dev.fly.dev/api/v1/billing/stripe/webhook`:
identidade, versão de API e todos os eventos de `payments.WebhookEvents`.
Produtos, preços e assinatura precisam
pertencer à mesma conta de teste. As garantias de armazenamento e a validação
nativa de login continuam obrigatórias. A entrega automática ainda não foi
implementada por esta mudança de documentação.

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

A auditoria usa credenciais de teste fornecidas pelo ambiente de teste aprovado,
sem exibir valores. Uma auditoria não prova a entrega automática em produção.
Ela retorna falha se preço, produto, evento ou endpoint não corresponder.

No Workbench, confira as tentativas do endpoint dev e exija respostas 2xx.
O inventário atual mostrou dez eventos `invoice.created` com uma entrega
pendente. Resolva essas entregas e confirme a finalização das faturas antes
de aprovar renovação e fim de trial. Faça o percurso de Checkout, upgrade,
downgrade, troca de periodicidade, volta ao Free, cancelamento e falha de
pagamento usando cartões e relógios de teste. Reinicie o app dev após uma
alteração e confira que conta, vínculo Stripe e plano continuam presentes.
