import type { Messages } from './en'

// Español. Anotado contra `Messages` para que el compilador exija paridad con
// el diccionario inglés: una clave o entrada de matriz que falte aquí es un
// error en tiempo de compilación, no un refallback silencioso al inglés.

export const es: Messages = {
  meta: {
    title: 'TerraScale | Infraestructura para Desarrolladores de Flujos de Datos',
    description:
      'TerraScale es una infraestructura centrada en el desarrollador para flujos de base de datos tipada, autenticación, colas y pagos en el modelo actual de regiones de lanzamiento.',
  },

  chrome: {
    brandHome: 'Inicio de TerraScale',
    nav: {
      home: 'Inicio',
      about: 'Acerca de',
      pricing: 'Precios',
      terms: 'Términos',
      contact: 'Contacto',
    },
    docs: 'Docs',
    status: 'Estado',
    getStarted: 'Empezar',
    toggleNav: 'Abrir menú de navegación',
    language: {
      trigger: 'Idioma',
      currentSuffix: 'Idioma: {label}',
      menu: 'Selecciona un idioma',
    },
  },

  footer: {
    tagline:
      'Capacitando a los equipos con infraestructura de base de datos segura y escalable. Escala tu tecnología sin esfuerzo.',
    rightsReserved: 'Todos los derechos reservados.',
    columns: {
      product: {
        title: 'Producto',
        links: {
          features: 'Características',
          pricing: 'Precios',
          changelog: 'Changelog',
          roadmap: 'Roadmap',
          documentation: 'Documentación',
        },
      },
      company: {
        title: 'Empresa',
        links: {
          about: 'Acerca de',
          blog: 'Blog',
          contact: 'Contacto',
        },
      },
      resources: {
        title: 'Recursos',
        links: {
          docs: 'Docs',
          apiReference: 'Referencia de la API',
          status: 'Estado',
          community: 'Comunidad',
          support: 'Soporte',
        },
      },
      legal: {
        title: 'Legal',
        links: {
          terms: 'Términos',
          support: 'Contacto',
        },
      },
    },
    social: {
      github: 'GitHub',
      discord: 'Discord',
      x: 'X',
      linkedin: 'LinkedIn',
      email: 'Correo',
    },
  },

  hero: {
    brandAlt: 'TerraScale',
    headlineLead: 'Escala tus datos,',
    headlineAccent: 'sin esfuerzo.',
    subtitle:
      'Infraestructura de base de datos tipada, autenticación, colas y pagos para equipos que construyen sobre la API de TerraScale.',
    ctaPrimary: 'Empezar gratis',
    ctaSecondary: 'Ver documentación',
    highlightsAria: 'Capacidades principales',
    highlights: ['Consultas TQL', 'Colas de proyecto', 'Regiones de lanzamiento'],
  },

  features: {
    eyebrow: 'Plataforma',
    titleLead: 'Hecho para escalar,',
    titleAccent: 'diseñado para desarrolladores',
    subtitle:
      'La superficie implementada abarca datos, autenticación, colas, pagos, SDKs y flujos del panel.',
    items: [
      {
        title: 'API de datos tipados',
        text: 'Crea bases de datos, repositorios, esquemas y documentos, y consulta con TQL a través de una única API pública.',
        bullets: ['Repositorios', 'TQL', 'Clientes SDK'],
      },
      {
        title: 'Autenticación y acceso',
        text: 'Los flujos de cuenta, sesión, passkey, MFA, organización, invitación y claves de API con ámbito comparten la misma superficie de la plataforma.',
        bullets: ['Passkeys', 'MFA', 'Claves con ámbito'],
      },
      {
        title: 'Regiones de lanzamiento',
        text: 'Usa las regiones de lanzamiento del producto San Francisco, Frankfurt y Guarulhos mediante flujos del panel y del SDK.',
        bullets: ['sfo', 'fra', 'gru'],
      },
      {
        title: 'Colas y pagos',
        text: 'Los flujos de cola se ejecutan en JetStream y los flujos de facturación de Stripe se procesan a través de TerraScale Payments.',
        bullets: ['NATS JetStream', 'Stripe', 'Webhooks'],
      },
    ],
  },

  code: {
    eyebrow: 'SDK para desarrolladores',
    headlineLead: 'Consulta tus datos',
    headlineAccent: 'en el lenguaje que amas',
    text: 'SDKs de primera clase en C# y TypeScript usan las mismas rutas HTTP públicas que el panel, con modelos OpenAPI generados por debajo.',
    bullets: [
      'Catálogo de contratos OpenAPI generado',
      'Clientes tipados en C# y TypeScript',
      'El panel usa el límite público del SDK',
      'Entradas de región para sfo, fra, gru',
    ],
    copy: 'Copiar',
    copied: 'Copiado',
    copyAria: 'Copiar código',
    tablistLabel: 'Ejemplos de lenguaje',
  },

  cta: {
    headlineLead: 'Try TerraScale',
    headlineAccent: 'on the free Developer plan.',
    text: 'Set it up in minutes on the free Developer plan and grow into Starter, Pro, Scale, or Business when you need more capacity. You will not be charged until you choose a paid plan.',
    ctaPrimary: 'Start free',
    ctaSecondary: 'Talk to Sales',
    perksAria: 'Plan details',
    perks: ['Free Developer tier', 'Upgrade anytime', 'No lock-in'],
  },

  pricing: {
    eyebrow: 'Precios',
    titleLead: 'Precios simples',
    titleAccent: 'que escalan contigo',
    subtitle: 'Empieza gratis, escala bajo demanda. Sin tarifas ocultas, sin sorpresas.',
    toggleLabel: 'Ciclo de facturación',
    monthly: 'Mensual',
    annual: 'Anual',
    save: 'Ahorra 20 %',
    badge: 'Más popular',
    custom: 'Personalizado',
    suffixMonthly: '/mes',
    suffixAnnual: '/año',
    baseNoteLead: 'desde',
    baseNoteTrail: '/mes + uso',
    plans: [
      {
        description: 'Free tier for prototypes and early evaluation',
        features: ['3 databases', '100K operations/month', '10 GB-region storage', '3 team members', 'Auth APIs'],
        cta: 'Start free',
      },
      {
        description: 'Small production workloads with Stripe billing',
        features: ['10 databases', '2M operations/month', '1 GB-region storage', '5 team members', 'Stripe billing'],
        cta: 'Choose Starter',
      },
      {
        description: 'More databases and team capacity',
        features: ['50 databases', '20M operations/month', '5 GB-region storage', '25 team members', '10 regions'],
        cta: 'Choose Pro',
      },
      {
        description: 'Default checkout plan with storage headroom',
        features: ['25 databases', '5M operations/month', '500 GB-region storage', '25 team members', '10 regions'],
        cta: 'Choose Scale',
      },
      {
        description: 'Highest seeded limits for larger organizations',
        features: ['100 databases', '50M operations/month', '5 TB-region storage', '100 team members', '10 regions'],
        cta: 'Choose Business',
      },
    ],
    triad: [
      { title: 'Controles de región', text: 'Crea bases de datos en sfo, fra o gru' },
      { title: 'Observable por defecto', text: 'Trazas y registros de OpenTelemetry en los flujos principales' },
      { title: 'Protecciones de uso', text: 'Planes medidos con controles de facturación a nivel de plataforma' },
    ],
    detailed: {
      eyebrow: 'Detailed Pricing',
      titleLead: 'Clear pricing for custom',
      titleAccent: 'deployments',
      text: 'Usage-based costs, support scopes, and deployment requirements are broken out below so enterprise teams can estimate spend before talking to sales.',
      headItem: 'Item',
      headPricing: 'Pricing',
      headDetails: 'Details',
      rows: [
        {
          item: 'Business plan base',
          unit: '/month',
          details: 'Seeded Business plan starts at $99/month ($990/year) before usage overage.',
        },
        {
          item: 'Storage overage',
          unit: '/GB-region',
          details: 'Paid plans use seed storage overage ($0.70 per GB-region by default).',
        },
        {
          item: 'Operation overage',
          unit: '/ 1M ops',
          details: 'Paid plans use seed operation overage ($1.00 per million operation units).',
        },
      ],
      docsLink: 'Full Pricing Docs',
    },
    faq: {
      titleLead: 'Frequently Asked',
      titleAccent: 'Questions',
      subtitle: 'Get answers to common questions about our pricing and plans.',
      items: [
        {
          q: 'What is a Database Operation?',
          a: 'One Database Operation unit is each started 4 KiB (4,096 bytes) of a logical object successfully read or written. Lists, queries, and batches count each item independently. Writes count once per selected region; reads count only in the serving region. Failed, denied, invalid, throttled, and not-found requests are not billed.',
        },
        {
          q: 'What if I need more operations or storage?',
          a: 'Paid plans support overage at seed rates ($0.70 per GB-region of storage and $1.00 per million operation units). Prefer upgrading when you consistently approach included limits.',
        },
        {
          q: 'When can I change my subscription?',
          a: 'Anytime through Stripe checkout and billing portal sessions on the organization billing routes. Upgrades take effect immediately with Stripe-prorated billing; downgrades follow Stripe period rules.',
        },
        {
          q: 'Can I export my data and leave anytime?',
          a: 'Yes. Your data is yours. Export through the dashboard or API. Rate limits protect cluster stability, but there is no exit fee for leaving TerraScale.',
        },
        {
          q: 'Do you offer larger custom deployments?',
          a: 'Contact sales when you need limits beyond the seeded Business plan, dedicated support, or custom commercial terms.',
        },
        {
          q: 'Do I need a credit card for the free Developer plan?',
          a: 'No. The Developer plan is free. Add a payment method only when you upgrade to a paid plan through Stripe checkout.',
        },
      ],
      footerTitle: 'Still have questions?',
      footerText: 'Our team is here to help you find the perfect plan for your needs.',
      contactSales: 'Contact Sales',
      readDocs: 'Read Documentation',
    },
    finalCta: {
      titleLead: '¿Listo para',
      titleAccent: 'empezar?',
      text: 'Únete a miles de desarrolladores construyendo el futuro con TerraScale.',
      cta: 'Empieza a construir hoy',
    },
  },

  contact: {
    eyebrow: 'Ponte en contacto',
    titleLead: 'Construyamos',
    titleAccent: 'algo juntos.',
    subtitle:
      '¿Tienes preguntas? ¿Necesitas un plan personalizado? ¿Quieres integrarte con TerraScale? Nos encantaría saber de ti.',
    formTitle: 'Envíanos un mensaje',
    labels: {
      name: 'Nombre',
      email: 'Correo',
      company: 'Empresa',
      message: 'Mensaje',
    },
    placeholders: {
      name: 'Mario Belchior',
      email: 'mario@acme.com',
      company: 'Acme Inc.',
      message: 'Cuéntanos sobre tu proyecto, tamaño del equipo y lo que esperas lograr...',
    },
    submit: {
      idle: 'Enviar mensaje',
      submitting: 'Enviando…',
      success: '¡Mensaje enviado!',
      error: 'Intentar de nuevo',
      badge: 'Mensaje enviado correctamente',
    },
    success: '¡Gracias! Tu mensaje ha sido registrado — responderemos en un plazo de 24 h.',
    errorNoForm: 'No se pudo enviar el formulario. Escríbenos directamente por correo.',
    errorSend: 'Algo salió mal al enviar tu mensaje. Inténtalo de nuevo o escríbenos directamente por correo.',
    note: 'Tu información nunca se comparte. Respuesta en un plazo de 24 h.',
    channelsTitle: 'Otras formas de contactarnos',
    channels: [
      { value: 'support@terrascale.tech' },
      { value: 'Únete a nuestra comunidad' },
      { value: 'Abrir issues y PRs' },
    ],
    slaHeading: 'SLA de soporte',
    slaBodyLead: 'Los clientes Pro tienen',
    slaBodyStrong: 'enrutamiento prioritario',
    slaBodyTrail: 'con primera respuesta en 1 hora durante el horario comercial.',
  },

  about: {
    eyebrow: 'Acerca de TerraScale',
    titleLead: 'Infraestructura de base de datos',
    titleAccent: 'distribuida globalmente.',
    subtitle:
      'TerraScale existe para que el DBaaS distribuido globalmente se sienta como un producto coherente: base de datos, autenticación y pagos construidos juntos sobre TerraBase, con facturación basada en uso y precios planos entre regiones.',
    ctaPricing: 'Ver precios',
    ctaContact: 'Habla con nosotros',
    missionEyebrow: 'Misión',
    missionTitle: 'Una única superficie de plataforma para equipos que entregan entre regiones.',
    missionText:
      'Los equipos no deberían tener que coser una base de datos, una capa de identidad, un sistema de facturación y un plan de despliegue regional antes de poder atender a sus clientes. TerraScale reúne esas preocupaciones en una única plataforma orientada al desarrollador, para que los equipos de producto puedan empezar pequeño, elegir las regiones que importan y escalar sin cambiar su modelo de aplicación.',
    pillars: [
      {
        title: 'Fundación TerraBase',
        text: 'TerraScale se construye sobre TerraBase para que los equipos ejecuten flujos de datos duraderos de documentos, consultas y operaciones detrás de una única superficie pública de la plataforma.',
      },
      {
        title: 'Autenticación incluida',
        text: 'El acceso a la base de datos, los usuarios, las sesiones, las invitaciones y las credenciales de máquina se planifican como un único límite de producto, en lugar de código de pegamento separado.',
      },
      {
        title: 'Facturación consciente del uso',
        text: 'Los planes se mantienen predecibles mientras el uso medido permite a los equipos crecer por carga de trabajo, no negociando el precio región por región.',
      },
    ],
    pricingPrincipleEyebrow: 'Principio de precios',
    bandTitle: 'Precios regionales planos, medidos por el uso real.',
    bandText:
      'La elección de región debería ser una decisión de arquitectura, no una línea sorpresa en la factura. El precio público de TerraScale se diseña en torno a los mismos precios de plan entre las regiones de lanzamiento, con almacenamiento, operaciones y transferencia creciendo según la carga de trabajo.',
    regions: ['San Francisco', 'Frankfurt', 'Guarulhos'],
  },

  notFound: {
    eyebrow: 'Página no encontrada',
    title: 'No podemos encontrar esa página.',
    text: 'La página que buscas se ha movido o nunca existió. Vamos a ponerte de vuelta en camino.',
    backHome: 'Volver al inicio',
    contactSupport: 'Contactar con soporte',
    linksLabel: 'Páginas populares:',
    links: {
      home: 'Inicio',
      pricing: 'Precios',
      contact: 'Contacto',
    },
  },
}
