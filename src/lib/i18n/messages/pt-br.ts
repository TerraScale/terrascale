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
      'Um banco de documentos tipado com replicação assíncrona em São Paulo, San Francisco Bay Area e Frankfurt. O endpoint global encaminha para a região selecionada saudável mais próxima. O Public Alpha não tem percentual de disponibilidade nem SLA.',
    ctaPrelaunch: 'Public Alpha',
    highlightsAria: 'Principais recursos',
    highlights: ['Roteamento por região selecionada', 'Replicação assíncrona', 'SDKs tipados', 'Pronto para agentes de IA'],
  },

  features: {
    eyebrow: 'Plataforma',
    titleLead: 'Public Alpha,',
    titleAccent: 'com limites explícitos',
    subtitle:
      'O contrato de lançamento cobre documentos, três regiões ativas, clientes tipados e saúde visível sem compromisso de disponibilidade.',
    items: [
      {
        title: 'Dados',
        text: 'Bancos de dados guardam coleções de documentos JSON, cada uma com um esquema opcional. Consulte com filtros JSON ou SQL somente leitura: SELECT, WHERE, ORDER BY e até 1.000 linhas por instrução.',
        bullets: ['Coleções', 'Filtros JSON', 'SQL somente leitura'],
      },
      {
        title: 'Regiões',
        text: 'São Paulo, San Francisco Bay Area e Frankfurt já atendem tráfego. Cada banco vive em uma região principal e é replicado por completo nas outras regiões que você selecionar.',
        bullets: ['gru', 'sjc', 'fra'],
      },
      {
        title: 'Roteamento',
        text: 'O endpoint global responde da região saudável mais próxima que tem o seu banco. Endpoints regionais estritos nunca atendem de uma região que você não selecionou.',
        bullets: ['Endpoint global', 'Endpoints regionais estritos'],
      },
      {
        title: 'Saúde',
        text: 'A replicação é assíncrona. Atraso, convergência, conflitos e latência da API são publicados como valores medidos separados, com o horário da observação. Não há promessa de atraso limitado, porcentagem de disponibilidade nem SLA.',
        bullets: ['Replicação assíncrona', 'Horário da observação', 'Sem SLA'],
      },
      {
        title: 'Segurança',
        text: 'O tráfego do cliente exige TLS 1.2 ou superior, os dados em repouso usam criptografia qualificada e toda requisição exige autorização. O conteúdo do cliente permanece nas regiões que você selecionar.',
        bullets: ['TLS 1.2+', 'Criptografia em repouso', 'Autorização por requisição'],
      },
    ],
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
    text: 'Free não exige cartão. Franquias e tarifas de uso pago estão aprovadas; cobranças por uso ainda não são coletadas.',
    ctaPrimary: 'Cadastre-se no Public Alpha',
    ctaSecondary: 'Fale com vendas',
    perksAria: 'Detalhes dos planos',
    perks: ['Free sem cartão', 'Starter anual; Pro e Team mensais'],
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
    subtitle: 'Free, Starter, Pro e Team para o Public Alpha. Enterprise exige contato com vendas e não está disponível para contratação direta.',
    badge: 'Mais popular',
    suffixMonthly: '/mês',
    suffixAnnual: '/ano',
    plans: {
      free: {
        description: 'Crie e avalie sem cartão de pagamento',
        cta: 'Começar com Free',
      },
      starter: {
        description: 'Assinatura exclusivamente anual para projetos pequenos',
        cta: 'Escolher Starter',
      },
      pro: {
        description: 'Assinatura mensal para projetos em crescimento',
        cta: 'Escolher Pro',
      },
      team: {
        description: 'Assinatura mensal para equipes',
        cta: 'Escolher Team',
      },
      enterprise: {
        description: 'Suporte mais uso, contratado com vendas',
        features: ['Consulte disponibilidade e condições com vendas', 'Sem contratação direta durante o Public Alpha'],
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
        text: 'Planos pagos incluem franquias e depois pagamento por uso; cobranças por uso ainda não são coletadas',
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
    },
    detailed: {
      eyebrow: 'Regras de cobrança',
      titleLead: 'Tarifas de uso',
      titleAccent: 'aprovadas',
      text: 'Planos pagos incluem as franquias e depois pagamento por uso às tarifas abaixo. Free não tem excedentes automáticos. Cobranças por uso ainda não são coletadas.',
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
          a: 'Planos pagos incluem franquias mensais e depois pagamento por uso. Franquias e tarifas estão aprovadas; cobranças por uso ainda não são coletadas. Free não tem excedentes automáticos.',
        },
        {
          q: 'Como as assinaturas são cobradas?',
          a: 'Free não exige cartão. Starter é cobrado somente por ano; seu valor mensal é um equivalente, não uma contratação mensal. Pro e Team são mensais. Não há testes gratuitos nem descontos anuais para Pro ou Team.',
        },
        {
          q: 'Posso exportar meus dados?',
          a: 'Sim. Leituras, exportação, exclusão e redução de uso permanecem disponíveis durante limites e períodos de leitura.',
        },
        {
          q: 'O que Enterprise inclui?',
          a: 'Enterprise tem uma base mensal de suporte de ${enterprisePriceUsd} mais uso. Consulte disponibilidade e condições com vendas; não há contratação direta durante o Public Alpha.',
        },
      ],
      footerTitle: 'Ainda tem dúvidas?',
      footerText: 'Fale conosco para confirmar disponibilidade e condições de contratação.',
      contactSales: 'Falar com vendas',
      readDocs: 'Ler documentação',
    },
    finalCta: {
      titleLead: 'Pronto para',
      titleAccent: 'começar?',
      text: 'Comece com Free ou confirme a contratação de um plano pago.',
      cta: 'Cadastre-se no Public Alpha',
    },
    status: 'Os preços das assinaturas, as franquias e as tarifas de excedentes estão aprovados. Cobranças por uso ainda não são coletadas.',
    cadence: {
      free: 'Sem cartão de pagamento',
      year: 'Somente {annual}/ano · equivalente a {monthly}/mês',
      month: 'Cobrança mensal',
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
        text: 'Free não exige cartão. Franquias e tarifas de uso pago estão aprovadas; cobranças por uso ainda não são coletadas.',
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
