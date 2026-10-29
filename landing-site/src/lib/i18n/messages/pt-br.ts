import type { Messages } from './en'

export const ptBr: Messages = {
  meta: {
    title: 'TerraScale Public Alpha | Banco de Documentos Regional',
    description:
      'O TerraScale Public Alpha oferece um banco de documentos tipado com replicação assíncrona em São Paulo, San Francisco Bay Area e Frankfurt.',
  },

  chrome: {
    brandHome: 'Página inicial da TerraScale',
    nav: {
      home: 'Início',
      about: 'Sobre',
      pricing: 'Preços',
      terms: 'Termos',
      contact: 'Contato',
    },
    docs: 'Docs',
    status: 'Status',
    login: 'Entrar',
    getStarted: 'Comece a construir',
    toggleNav: 'Abrir menu de navegação',
    language: {
      trigger: 'Idioma',
      currentSuffix: 'Idioma: {label}',
      menu: 'Selecione um idioma',
    },
  },

  footer: {
    tagline:
      'O TerraScale Public Alpha oferece um banco de documentos regional sem percentual de disponibilidade nem SLA. A disponibilidade atual é publicada na página de status.',
    rightsReserved: 'Todos os direitos reservados.',
    columns: {
      product: {
        title: 'Produto',
        links: {
          features: 'Recursos',
          pricing: 'Preços',
          changelog: 'Changelog',
          documentation: 'Documentação',
        },
      },
      company: {
        title: 'Empresa',
        links: {
          about: 'Sobre',
          blog: 'Blog',
          contact: 'Contato',
          terms: 'Termos',
          privacy: 'Privacidade',
          dpa: 'DPA',
        },
      },
      resources: {
        title: 'Recursos',
        links: {
          docs: 'Docs',
          apiReference: 'Referência da API',
          status: 'Status',
          community: 'Comunidade',
          support: 'Suporte',
          supportEmail: 'Contato: support@terrascale.tech',
        },
      },
    },
    social: {
      github: 'GitHub',
      discord: 'Discord',
      x: 'X',
      linkedin: 'LinkedIn',
      email: 'E-mail',
    },
  },

  hero: {
    eyebrow: 'Public Alpha',
    headlineLead: 'Banco de dados de documentos tipado,',
    headlineAccent: 'replicado entre regiões.',
    subtitle:
      'Escolha as regiões onde seus dados vivem. O endpoint global atende cada requisição pela mais próxima e saudável, e as gravações são replicadas de forma assíncrona para as demais.',
    cta: 'Comece grátis',
    ctaNote: 'Plano Free sem cartão. O Public Alpha não tem percentual de disponibilidade nem SLA.',
    highlightsAria: 'Principais recursos',
    highlights: ['Roteamento por região selecionada', 'Replicação assíncrona', 'SDKs tipados', 'Pronto para agentes de IA'],
  },

  features: {
    eyebrow: 'Plataforma',
    titleLead: 'O que o Public Alpha cobre,',
    titleAccent: 'e onde ele para.',
    subtitle:
      'O contrato de lançamento cobre documentos, três regiões ativas, clientes tipados e saúde visível sem compromisso de disponibilidade.',
    items: [
      {
        title: 'Dados',
        headline: 'Consulte documentos JSON com filtros ou SQL',
        text: 'Bancos de dados guardam coleções de documentos JSON, cada uma com um esquema opcional. Consulte com filtros JSON ou SQL somente leitura: SELECT, WHERE, ORDER BY e até 1.000 linhas por instrução.',
        bullets: ['Coleções', 'Filtros JSON', 'SQL somente leitura'],
      },
      {
        title: 'Regiões',
        headline: 'Mantenha cada banco nas regiões que você escolher',
        text: 'São Paulo, San Francisco Bay Area e Frankfurt já atendem tráfego. Cada banco vive em uma região principal e é replicado por completo nas outras regiões que você selecionar.',
        bullets: ['gru', 'sjc', 'fra'],
      },
      {
        title: 'Roteamento',
        headline: 'Atenda leituras pela região saudável mais próxima',
        text: 'O endpoint global responde da região saudável mais próxima que tem o seu banco. Endpoints regionais estritos nunca atendem de uma região que você não selecionou.',
        bullets: ['Endpoint global', 'Endpoints regionais estritos'],
      },
      {
        title: 'Saúde',
        headline: 'Veja a saúde da replicação como valores medidos',
        text: 'A replicação é assíncrona. Atraso, convergência, conflitos e latência da API são publicados como valores medidos separados, com o horário da observação. Não há promessa de atraso limitado, porcentagem de disponibilidade nem SLA.',
        bullets: ['Replicação assíncrona', 'Horário da observação', 'Sem SLA'],
      },
      {
        title: 'Segurança',
        headline: 'Criptografia em trânsito e em repouso',
        text: 'O tráfego do cliente exige TLS 1.2 ou superior, os dados em repouso usam criptografia qualificada e toda requisição exige autorização. O conteúdo do cliente permanece nas regiões que você selecionar.',
        bullets: ['TLS 1.2+', 'Criptografia em repouso', 'Autorização por requisição'],
      },
    ],
  },

  howItWorks: {
    eyebrow: 'Como funciona',
    titleLead: 'Do cadastro a leituras globais',
    titleAccent: 'em três passos.',
    subtitle: 'Cada banco tem uma região principal mais as regiões de réplica que você selecionar. O conteúdo do cliente permanece nessas regiões.',
    steps: [
      {
        title: 'Crie um banco de dados',
        text: 'Cadastre-se sem cartão, crie um projeto e adicione um banco. Escolha a região principal: São Paulo, San Francisco Bay Area ou Frankfurt.',
      },
      {
        title: 'Selecione regiões de réplica',
        text: 'Adicione as outras regiões que devem guardar uma cópia completa. Uma gravação confirma o aceite local e depois é replicada de forma assíncrona para cada região selecionada.',
      },
      {
        title: 'Leia e grave',
        text: 'Use o endpoint global para a região selecionada saudável mais próxima, ou um endpoint regional estrito que nunca atende de outra região. Chame pelo SDK de C# ou TypeScript, pela API HTTP ou por um cliente MCP.',
        tags: ['Endpoint global', 'Endpoint regional estrito'],
      },
    ],
  },

  tagline: {
    lines: ['Seus dados ficam nas regiões que você escolhe.', 'As leituras vêm da mais próxima e saudável.'],
  },

  homeFaq: {
    eyebrow: 'FAQ',
    titleLead: 'Perguntas antes',
    titleAccent: 'de começar.',
    subtitle: 'Respostas curtas sobre regiões, replicação e o que o Public Alpha cobre.',
    items: [
      {
        q: 'O que é a TerraScale?',
        a: 'Um banco de dados de documentos tipado. Bancos guardam coleções de documentos JSON, cada uma com um esquema opcional. Você consulta com filtros JSON ou com SQL somente leitura: SELECT, WHERE, ORDER BY e até 1.000 linhas por instrução.',
      },
      {
        q: 'Quais regiões atendem hoje?',
        a: 'São Paulo (gru), San Francisco Bay Area (sjc) e Frankfurt (fra). Cada banco vive em uma região principal e é replicado por completo nas outras regiões que você selecionar.',
      },
      {
        q: 'Como funciona a replicação?',
        a: 'A replicação é assíncrona. Uma gravação bem sucedida confirma o aceite local, e as outras regiões selecionadas a recebem depois. Atraso, convergência, conflitos e latência da API são publicados como valores medidos separados, com o horário da observação.',
      },
      {
        q: 'Qual a diferença entre o endpoint global e os regionais?',
        a: 'O endpoint global responde da região saudável mais próxima que tem o seu banco. Um endpoint regional estrito nunca atende de uma região que você não selecionou.',
      },
      {
        q: 'Existe compromisso de disponibilidade?',
        a: 'Ainda não. O Public Alpha não tem percentual de disponibilidade, promessa de atraso limitado nem SLA. A disponibilidade atual é publicada na página de status.',
      },
      {
        q: 'Quais linguagens e ferramentas posso usar?',
        a: 'SDKs tipados em C# e TypeScript chamam a mesma API HTTP pública usada pelo painel. Você também pode usar a CLI, ou conectar o Claude Code, o Cursor ou qualquer cliente MCP ao servidor MCP da TerraScale com um token de acesso pessoal.',
      },
      {
        q: 'Como meus dados são protegidos?',
        a: 'O tráfego do cliente exige TLS 1.2 ou superior, os dados em repouso usam criptografia qualificada e toda requisição exige autorização. O conteúdo do cliente permanece nas regiões que você selecionar.',
      },
      {
        q: 'Quanto custa para começar?',
        a: 'Free não exige cartão. Starter, Pro e Team são cobrados mensal ou anualmente. Unidades de operação acima de uma franquia paga vão para a próxima fatura; excedentes de armazenamento e saída para clientes ainda não são cobrados.',
      },
      {
        q: 'Posso exportar meus dados e sair?',
        a: 'Sim. Exporte pelo painel ou pela API a qualquer momento. Não há taxa de saída para deixar a TerraScale.',
      },
    ],
    footerTitle: 'Ainda tem dúvidas?',
    footerText: 'Fale conosco sobre regiões, planos ou condições Enterprise.',
    contactSales: 'Fale conosco',
    readDocs: 'Ler a documentação',
  },

  code: {
    eyebrow: 'SDK para desenvolvedores',
    headlineLead: 'Consulte seus dados',
    headlineAccent: 'na linguagem que você ama',
    text: 'SDKs tipados em C# e TypeScript chamam a mesma API HTTP pública usada pelo painel, com modelos gerados a partir do contrato OpenAPI.',
    bullets: ['Clientes tipados em C# e TypeScript', 'Modelos OpenAPI gerados', 'Filtros JSON ou SQL somente leitura'],
    agent: {
      title: 'Ou peça ao seu agente de IA favorito para fazer isso por você',
      text: 'Suportamos CLI e MCP. Conecte o Claude Code, o Cursor ou qualquer cliente MCP ao servidor MCP da TerraScale com um token de acesso pessoal. O agente age com os seus papéis e cada chamada é verificada novamente. Agentes também podem chamar a API HTTP pelo terminal.',
      promptsLabel: 'Experimente pedir',
      prompts: [
        'Liste meus bancos de dados e as regiões que cada um atende.',
        'Encontre os clientes com 10 ou mais assentos no banco app.',
        'Quanto da cota de unidades de operação deste mês já usamos?',
      ],
      toolsLabel: 'Algumas ferramentas MCP',
    },
    copy: 'Copiar',
    copied: 'Copiado',
    copyAria: 'Copiar código',
    tablistLabel: 'Exemplos de código',
  },

  cta: {
    headlineLead: 'Cadastre-se no',
    headlineAccent: 'TerraScale Public Alpha.',
    text: 'Free não exige cartão. Planos pagos cobram na próxima fatura as unidades de operação acima da franquia; excedentes de armazenamento e saída para clientes ainda não são cobrados.',
    ctaPrimary: 'Cadastre-se no Public Alpha',
    ctaSecondary: 'Fale com vendas',
    perksAria: 'Detalhes dos planos',
    perks: ['Free sem cartão', 'Planos pagos mensais ou anuais', 'Exporte seus dados quando quiser'],
  },

  pricingTeaser: {
    eyebrow: 'Preços',
    title: 'Comece com Free. Compare as assinaturas.',
    cta: 'Comparar planos',
  },

  pricing: {
    eyebrow: 'Preços',
    titleLead: 'Preços simples',
    titleAccent: 'que escalam com VOCÊ',
    subtitle: 'Comece com Free ou contrate Starter, Pro ou Team pelo checkout. Enterprise é contratado com vendas.',
    badge: 'Mais popular',
    suffixMonthly: '/mês',
    suffixAnnual: '/ano',
    intervals: {
      label: 'Intervalo de cobrança',
      month: 'Mensal',
      year: 'Anual',
      saving: '{months} meses grátis',
    },
    plans: {
      free: {
        description: 'Crie e avalie sem cartão de pagamento',
        cta: 'Começar com Free',
      },
      starter: {
        description: 'Assinatura para projetos pequenos',
        cta: 'Escolher Starter',
      },
      pro: {
        description: 'Assinatura para projetos em crescimento',
        cta: 'Escolher Pro',
      },
      team: {
        description: 'Assinatura para equipes',
        cta: 'Escolher Team',
      },
      enterprise: {
        description: 'Suporte mais uso, contratado com vendas',
        features: ['Consulte condições com vendas', 'Limites e meta de resposta de suporte sob medida'],
        cta: 'Falar com vendas',
      },
    },
    limits: {
      projects: ['{n} projeto', '{n} projetos'],
      databases: ['{n} banco de dados', '{n} bancos de dados'],
      members: ['{n} membro', '{n} membros'],
      storage: '{n} entre regiões (média mensal)',
      operationUnits: '{n} unidades de operação/mês',
      egress: '{n} de saída para clientes/mês',
    },
    usage: {
      eyebrow: 'Como o uso é medido',
      title: 'Armazenamento, operações e saída para clientes.',
    },
    triad: [
      {
        title: 'Armazenamento entre regiões',
        text: 'GB decimais em cada região selecionada, com média ao longo do mês',
      },
      {
        title: 'Unidades de operação',
        text: 'Cada 4 KiB iniciados lidos ou escritos; escritas contam uma vez e a replicação é grátis',
      },
      {
        title: 'Franquias e uso adicional',
        text: 'Unidades de operação acima da franquia paga vão para a próxima fatura; excedentes de armazenamento e saída para clientes ainda não são cobrados',
      },
    ],
    compare: {
      eyebrow: 'Comparar',
      title: 'Franquias por plano.',
      head: 'Franquia',
      projects: 'Projetos',
      databases: 'Bancos de dados',
      members: 'Membros',
      storage: 'Armazenamento entre regiões (média mensal)',
      operationUnits: 'Unidades de operação / mês',
      egress: 'Saída para clientes / mês',
      billing: 'Ciclo de cobrança',
      entitlementsHead: 'Direitos do plano',
      requestsPerSecond: 'Requisições / segundo',
      burstRequests: 'Requisições em rajada',
      concurrentRequests: 'Requisições simultâneas',
      queryTimeout: 'Tempo limite de consulta',
      storageLimit: 'Limite de armazenamento',
      pitr: 'Recuperação para um ponto no tempo',
      backupRetention: 'Retenção de backups',
      regionsPerDatabase: 'Regiões por banco de dados',
      support: 'Meta de resposta do suporte',
      noCeiling: 'Sem limite do plano',
      seconds: '{n} s',
      days: ['{n} dia', '{n} dias'],
      supportTargets: {
        Community: 'Comunidade',
        '2 business days': '2 dias úteis',
        '1 business day': '1 dia útil',
        '8 hours': '8 horas',
        Custom: 'Sob medida',
      },
      entitlementsNote: 'As taxas de requisições e a simultaneidade valem por organização; requisições além delas recebem HTTP 429 com um cabeçalho Retry-After. O limite de armazenamento é um teto rígido, separado da franquia incluída. As metas de resposta do suporte não são um SLA.',
    },
    detailed: {
      eyebrow: 'Regras de cobrança',
      titleLead: 'Tarifas de uso',
      titleAccent: 'aprovadas',
      text: 'Planos pagos incluem as franquias. Unidades de operação acima da franquia são adicionadas à próxima fatura da assinatura pela tarifa abaixo; excedentes de armazenamento e saída para clientes ainda não são cobrados. O Free interrompe novas gravações ao atingir sua franquia de operações e não tem cobranças de excedentes.',
      headItem: 'Item',
      headPricing: 'Preço',
      headDetails: 'Detalhes',
      rows: [
        {
          item: 'Unidades de operação',
          unit: '/milhão de unidades',
          details: 'Cada 4 KiB iniciados de cada leitura ou escrita bem-sucedida contam separadamente. Escritas contam uma vez, independentemente das regiões selecionadas; leituras contam na região que atende. A replicação é grátis.',
        },
        {
          item: 'Armazenamento entre regiões',
          unit: '/GB-região-mês',
          details: 'GB decimais armazenados em cada região selecionada, com média ao longo do mês.',
        },
        {
          item: 'Saída para clientes',
          unit: '/GB',
          details: 'Bytes decimais de respostas da aplicação enviados aos clientes; exclui tráfego de replicação.',
        },
      ],
      docsLink: 'Documentação completa de preços',
    },
    faq: {
      eyebrow: 'Perguntas frequentes',
      titleLead: 'Perguntas',
      titleAccent: 'frequentes',
      subtitle: 'Respostas diretas sobre os planos do Public Alpha.',
      items: [
        {
          q: 'O que é uma unidade de operação?',
          a: 'Uma unidade de operação é cada 4 KiB (4.096 bytes) iniciados de um objeto lógico lido ou gravado com sucesso. Listas, consultas e lotes contam cada item separadamente. Gravações contam uma vez, independentemente do número de regiões selecionadas; leituras contam apenas na região que as atende. A replicação entre regiões não é cobrada. Solicitações com falha, negadas, inválidas, limitadas e não encontradas não são cobradas.',
        },
        {
          q: 'E se eu precisar de mais operações, armazenamento ou saída?',
          a: 'Planos pagos incluem franquias mensais. Unidades de operação acima da franquia são adicionadas à próxima fatura da assinatura pela tarifa de excedentes; excedentes de armazenamento e saída para clientes ainda não são cobrados. O Free interrompe novas gravações ao atingir sua franquia de operações e não tem cobranças de excedentes.',
        },
        {
          q: 'Como as assinaturas são cobradas?',
          a: 'Free não exige cartão. Starter, Pro e Team são cobrados por mês ou por ano; um ano custa dez mensalidades, então a cobrança anual economiza dois meses. Não há testes gratuitos.',
        },
        {
          q: 'Posso exportar meus dados?',
          a: 'Sim. Leituras, exportação, exclusão e redução de uso permanecem disponíveis durante limites e períodos de leitura.',
        },
        {
          q: 'O que Enterprise inclui?',
          a: 'Enterprise tem uma base mensal de suporte de ${enterprisePriceUsd} mais uso. Consulte as condições com vendas; o Enterprise não é vendido pelo checkout.',
        },
      ],
      footerTitle: 'Ainda tem dúvidas?',
      footerText: 'Fale conosco sobre planos, limites ou condições do Enterprise.',
      contactSales: 'Falar com vendas',
      readDocs: 'Ler documentação',
    },
    finalCta: {
      titleLead: 'Pronto para',
      titleAccent: 'começar?',
      text: 'Comece com Free ou escolha um plano pago no checkout.',
      cta: 'Cadastre-se no Public Alpha',
    },
    status: 'Planos pagos estão disponíveis pelo checkout. Unidades de operação acima da franquia são adicionadas à próxima fatura; excedentes de armazenamento e saída para clientes ainda não são cobrados.',
    cadence: {
      free: 'Sem cartão de pagamento',
      year: 'Cobrança anual · equivalente a {monthly}/mês · {months} meses grátis',
      month: 'Cobrança mensal',
      both: '{monthly}/mês ou {annual}/ano',
      contact: 'Suporte + uso · fale com vendas',
    },
    suffixUsage: '/mês + uso',
  },

  contact: {
    eyebrow: 'Entre em contato',
    titleLead: 'Vamos construir',
    titleAccent: 'algo juntos.',
    subtitle:
      'Tem dúvidas? Precisa de um plano personalizado? Quer integrar com a TerraScale? Adoraríamos ouvir você.',
    labels: {
      name: 'Nome',
      email: 'E-mail',
      company: 'Empresa',
      message: 'Mensagem',
    },
    placeholders: {
      name: 'Mario Belchior',
      email: 'mario@acme.com',
      company: 'Acme Inc.',
      message: 'Conte sobre seu projeto, tamanho da equipe e o que você espera alcançar...',
    },
    submit: {
      idle: 'Enviar mensagem',
      submitting: 'Enviando…',
      success: 'Mensagem enviada!',
      error: 'Tentar novamente',
      badge: 'Mensagem enviada com sucesso',
    },
    success: 'Obrigado! Sua mensagem foi enviada com segurança para nossa equipe.',
    errorNoForm: 'Não foi possível enviar o formulário. Envie um e-mail diretamente para nós.',
    errorSend: 'Algo deu errado ao enviar sua mensagem. Tente novamente ou nos envie um e-mail diretamente.',
    note: 'O tráfego do cliente exige TLS 1.2 ou mais recente. Dados em repouso usam criptografia qualificada pelo serviço.',
    channelsTitle: 'Fale conosco',
    channels: [
      { value: 'support@terrascale.tech' },
      { value: 'Entre na nossa comunidade' },
      { value: 'Abrir issues e PRs' },
    ],
  },

  about: {
    eyebrow: 'Sobre a TerraScale',
    titleLead: 'Globalmente distribuída,',
    titleAccent: 'infraestrutura de banco de dados.',
    subtitle:
      'O TerraScale Public Alpha oferece um banco de documentos em São Paulo, San Francisco Bay Area e Frankfurt com replicação assíncrona.',
    ctaPricing: 'Ver preços',
    ctaContact: 'Fale conosco',
    missionEyebrow: 'Missão',
    missionTitle: 'Uma única superfície de plataforma para equipes que entregam entre regiões.',
    missionText:
      'Os projetos escolhem suas regiões. O endpoint global usa a região selecionada saudável mais próxima. Endpoints regionais estritos não atendem de outra região.',
    pillars: [
      {
        title: 'Fundação TerraBase',
        text: 'A TerraScale é construída sobre a TerraBase para que as equipes executem fluxos de dados duráveis de documentos, consultas e operações por trás de uma única superfície pública da plataforma.',
      },
      {
        title: 'Replicação assíncrona',
        text: 'Uma escrita bem-sucedida confirma aceitação local. A replicação para outras regiões selecionadas acontece depois.',
      },
      {
        title: 'Cobrança consciente do uso',
        text: 'Free não exige cartão. Planos pagos cobram na próxima fatura as unidades de operação acima da franquia; excedentes de armazenamento e saída para clientes ainda não são cobrados.',
      },
    ],
    pricingPrincipleEyebrow: 'Princípio de preços',
    bandTitle: 'Preços regionais fixos, medidos pelo uso real.',
    bandText:
      'O armazenamento soma os bytes em cada região selecionada. Distância de rede estimada, latência medida da API e saúde da replicação continuam separadas.',
  },

  notFound: {
    eyebrow: 'Página não encontrada',
    title: 'Não conseguimos encontrar essa página.',
    text: 'A página que você procura foi movida ou nunca existiu. Vamos colocá-lo de volta no caminho.',
    backHome: 'Voltar ao início',
    contactSupport: 'Contatar suporte',
  },
}
