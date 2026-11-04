import type { Messages } from './en'

export const es: Messages = {
  meta: {
    title: 'TerraScale Public Alpha | Base de datos SQL regional',
    description:
      'TerraScale Public Alpha ofrece bases de datos SQL, cada una almacenada y servida en una región principal: São Paulo, San Francisco Bay Area o Fráncfort.',
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
    login: 'Iniciar sesión',
    getStarted: 'Empieza a construir',
    toggleNav: 'Abrir menú de navegación',
    language: {
      trigger: 'Idioma',
      currentSuffix: 'Idioma: {label}',
      menu: 'Selecciona un idioma',
    },
  },

  footer: {
    rightsReserved: 'Todos los derechos reservados.',
    columns: {
      product: {
        title: 'Producto',
        links: {
          features: 'Características',
          pricing: 'Precios',
          changelog: 'Changelog',
          documentation: 'Documentación',
        },
      },
      company: {
        title: 'Empresa',
        links: {
          about: 'Acerca de',
          blog: 'Blog',
          contact: 'Contacto',
          terms: 'Términos',
          privacy: 'Privacidad',
          dpa: 'DPA',
        },
      },
      resources: {
        title: 'Recursos',
        links: {
          docs: 'Docs',
          apiReference: 'Referencia de la API',
          status: 'Estado',
          support: 'Soporte',
          supportEmail: 'Contacto: support@terrascale.tech',
        },
      },
    },
    social: {
      github: 'GitHub',
      linkedin: 'LinkedIn',
      email: 'Correo',
    },
  },

  hero: {
    headlineLead: 'Base de datos SQL,',
    headlineAccent: 'en la región que elijas.',
    cta: 'Empieza gratis',
  },

  features: {
    titleLead: 'Lo que cubre Public Alpha,',
    titleAccent: 'y dónde termina.',
    items: [
      {
        headline: 'SQL con tablas, índices y claves foráneas',
        text: 'Crea tablas, índices, vistas y triggers en el dialecto de SQLite. Las claves foráneas siempre se aplican, las columnas JSON funcionan con json_extract y hasta 256 sentencias se confirman como una transacción.',
      },
      {
        headline: 'Conéctate con psql, HTTP o la CLI',
        text: 'Los clientes de Postgres se conectan por el protocolo de red con una clave de API como contraseña. La API HTTP, la CLI y los SDK ejecutan el mismo SQL; los clientes MCP ejecutan consultas de solo lectura.',
      },
      {
        headline: 'Una región principal por base de datos',
        text: 'Elige São Paulo, San Francisco Bay Area o Fráncfort al crear una base de datos. Se almacena y se sirve en esa región.',
      },
      {
        headline: 'Estado publicado, no prometido',
        text: 'La disponibilidad actual y la latencia de API se publican en la página de estado. No hay porcentaje de disponibilidad, promesa de latencia ni SLA.',
      },
      {
        headline: 'Cifrado en tránsito y en reposo',
        text: 'El tráfico del cliente requiere TLS 1.2 o posterior, los datos en reposo usan cifrado calificado y cada solicitud requiere autorización.',
      },
    ],
  },

  howItWorks: {
    titleLead: 'Del registro a tu primera consulta',
    titleAccent: 'en tres pasos.',
    steps: [
      {
        title: 'Crea una base de datos',
        text: 'Regístrate sin tarjeta, crea un proyecto y añade una base de datos en su región principal: São Paulo, San Francisco Bay Area o Fráncfort.',
      },
      {
        title: 'Crea una clave de API',
        text: 'Limita la clave a la base de datos. Úsala como contraseña de Postgres o cámbiala por un token de acceso para HTTP y los SDK.',
      },
      {
        title: 'Ejecuta SQL',
        text: 'Crea tablas y consúltalas desde psql, la CLI, el SDK de C# o TypeScript o la API HTTP.',
      },
    ],
  },


  homeFaq: {
    titleLead: 'Preguntas antes',
    titleAccent: 'de empezar.',
    items: [
      {
        q: '¿Qué es TerraScale?',
        a: 'Una base de datos SQL. Cada base contiene tablas, índices, vistas y triggers en el dialecto de SQLite, con claves foráneas siempre aplicadas. Accedes por el protocolo de red de Postgres, HTTP, la CLI, los SDK o MCP.',
      },
      {
        q: '¿Qué regiones sirven hoy?',
        a: 'São Paulo (gru), San Francisco Bay Area (sjc) y Fráncfort (fra). Cada base de datos vive en una región principal, elegida al crearla.',
      },
      {
        q: '¿Mi base de datos se copia a otras regiones?',
        a: 'Hoy no. Una base de datos se almacena y se sirve solo en su región principal. Las copias de seguridad la protegen contra la pérdida de datos.',
      },
      {
        q: '¿Puedo usar mis herramientas de Postgres?',
        a: 'Sí, mediante el protocolo de red. psql y los drivers de Postgres se conectan a pg.terrascale.tech con una clave de API como contraseña. Las sentencias usan el dialecto de SQLite, así que la sintaxis exclusiva de Postgres se rechaza.',
      },
      {
        q: '¿Hay un compromiso de disponibilidad?',
        a: 'Todavía no. Public Alpha no tiene porcentaje de disponibilidad, promesa de latencia ni SLA. La disponibilidad actual se publica en la página de estado.',
      },
      {
        q: '¿Qué lenguajes y herramientas puedo usar?',
        a: 'Usa psql o cualquier driver de Postgres, los SDK tipados de C# y TypeScript, la API HTTP o la CLI. También puedes conectar Claude Code, Cursor o cualquier cliente MCP al servidor MCP de TerraScale con un token de acceso personal.',
      },
      {
        q: '¿Cómo se protegen mis datos?',
        a: 'El tráfico del cliente requiere TLS 1.2 o posterior, los datos en reposo usan cifrado calificado y cada solicitud requiere autorización.',
      },
      {
        q: '¿Cuánto cuesta empezar?',
        a: 'Free no requiere tarjeta. Starter, Pro y Team se facturan mensual o anualmente. Las unidades de operación por encima de una asignación pagada van en la próxima factura y el almacenamiento por encima de ella se factura por GB-mes; el excedente de salida a clientes aún no se factura.',
      },
      {
        q: '¿Puedo exportar mis datos e irme?',
        a: 'Sí. Exporta desde el panel o la API en cualquier momento. No hay tarifa de salida por dejar TerraScale.',
      },
    ],
    contactSales: 'Contáctanos',
  },

  code: {
    headlineLead: 'Consulta tus datos',
    headlineAccent: 'en el lenguaje que amas',
    bullets: ['Clientes tipados en C# y TypeScript', 'Protocolo de red de Postgres para psql y drivers', 'Hasta 256 sentencias en una transacción'],
    agent: {
      title: 'O pide a tu agente de IA favorito que lo haga por ti',
      text: 'Soportamos CLI y MCP. Conecta Claude Code, Cursor o cualquier cliente MCP al servidor MCP de TerraScale con un token de acceso personal. El agente actúa con tus roles y cada llamada se vuelve a verificar. Los agentes también pueden llamar a la API HTTP desde una terminal.',
      prompts: [
        'Lista mis bases de datos y sus regiones principales.',
        'Encuentra los clientes con 10 o más asientos en la base de datos app.',
        '¿Cuánto de la cuota de unidades de operación de este mes hemos usado?',
      ],
    },
    copy: 'Copiar',
    copied: 'Copiado',
    copyAria: 'Copiar código',
    tablistLabel: 'Ejemplos de código',
  },

  cta: {
    headlineLead: 'Regístrate en',
    headlineAccent: 'TerraScale Public Alpha.',
    ctaPrimary: 'Registrarse en Public Alpha',
  },

  pricingTeaser: {
    title: 'Empieza con Free. Compara las suscripciones.',
    cta: 'Comparar planes',
  },

  pricing: {
    titleLead: 'Precios simples',
    titleAccent: 'que escalan contigo',
    suffixMonthly: '/mes',
    suffixAnnual: '/año',
    intervals: {
      label: 'Intervalo de facturación',
      month: 'Mensual',
      year: 'Anual',
    },
    plans: {
      free: {
        cta: 'Empezar con Free',
      },
      starter: {
        cta: 'Elegir Starter',
      },
      pro: {
        cta: 'Elegir Pro',
      },
      team: {
        cta: 'Elegir Team',
      },
      enterprise: {
        features: ['Consultar condiciones con ventas', 'Límites y objetivo de respuesta de soporte a medida'],
        cta: 'Contactar ventas',
      },
    },
    limits: {
      projects: ['{n} proyecto', '{n} proyectos'],
      databases: ['{n} base de datos', '{n} bases de datos'],
      members: ['{n} miembro', '{n} miembros'],
      storage: '{n} de almacenamiento (promedio mensual)',
      operationUnits: '{n} unidades de operación/mes',
      egress: '{n} de salida a clientes/mes',
    },
    usage: {
      title: 'Almacenamiento, operaciones y salida a clientes.',
    },
    triad: [
      {
        title: 'Almacenamiento',
        text: 'GB decimales almacenados, promediados durante el mes',
      },
      {
        title: 'Unidades de operación',
        text: 'Cada 4 KiB iniciados leídos o escritos',
      },
      {
        title: 'Asignaciones y uso adicional',
        text: 'Las unidades de operación sobre la asignación pagada van a la próxima factura y el almacenamiento por encima de ella se factura por GB-mes; el excedente de salida a clientes aún no se factura',
      },
    ],
    compare: {
      title: 'Asignaciones por plan.',
      head: 'Asignación',
      projects: 'Proyectos',
      databases: 'Bases de datos',
      members: 'Miembros',
      storage: 'Almacenamiento (promedio mensual)',
      operationUnits: 'Unidades de operación / mes',
      egress: 'Salida a clientes / mes',
      billing: 'Ciclo de facturación',
      entitlementsHead: 'Derechos del plan',
      requestsPerSecond: 'Solicitudes / segundo',
      burstRequests: 'Solicitudes en ráfaga',
      concurrentRequests: 'Solicitudes concurrentes',
      queryTimeout: 'Tiempo límite de consulta',
      storageLimit: 'Límite de almacenamiento',
      pitr: 'Recuperación a un punto en el tiempo',
      backupRetention: 'Retención de copias de seguridad',
      regionsPerDatabase: 'Regiones por base de datos',
      support: 'Objetivo de respuesta de soporte',
      noCeiling: 'Sin límite del plan',
      seconds: '{n} s',
      days: ['{n} día', '{n} días'],
      supportTargets: {
        Community: 'Comunidad',
        '2 business days': '2 días hábiles',
        '1 business day': '1 día hábil',
        '8 hours': '8 horas',
        Custom: 'A medida',
      },
      entitlementsNote: 'Las tasas de solicitudes y la concurrencia se aplican por organización; las solicitudes que las superan reciben HTTP 429 con un encabezado Retry-After. El límite de almacenamiento es un tope estricto, distinto de la asignación incluida. Los objetivos de respuesta de soporte no son un SLA.',
    },
    detailed: {
      titleLead: 'Tarifas de uso',
      titleAccent: 'aprobadas',
      text: 'Los planes pagados incluyen las asignaciones. Las unidades de operación por encima de la asignación se suman a la próxima factura de la suscripción a esta tarifa y el almacenamiento por encima de ella se factura por GB-mes; el excedente de salida a clientes aún no se factura. Free detiene las nuevas escrituras al llegar a su asignación de operaciones y no tiene cargos por excedentes.',
      headItem: 'Item',
      headPricing: 'Precio',
      headDetails: 'Detalles',
      rows: [
        {
          item: 'Unidades de operación',
          unit: '/millón de unidades',
          details: 'Cada 4 KiB iniciados de cada lectura o escritura exitosa cuentan por separado.',
        },
        {
          item: 'Almacenamiento',
          unit: '/GB-mes',
          details: 'GB decimales almacenados, promediados durante el mes.',
        },
        {
          item: 'Salida a clientes',
          unit: '/GB',
          details: 'Bytes decimales de respuestas de la aplicación enviados a clientes.',
        },
      ],
      docsLink: 'Documentación completa de precios',
    },
    faq: {
      titleLead: 'Preguntas',
      titleAccent: 'frecuentes',
      items: [
        {
          q: '¿Qué es una unidad de operación?',
          a: 'Una unidad de operación es cada 4 KiB (4.096 bytes) iniciados de un objeto lógico leído o escrito correctamente. Las listas, consultas y lotes cuentan cada elemento por separado. Las solicitudes fallidas, denegadas, inválidas, limitadas y no encontradas no se facturan.',
        },
        {
          q: '¿Qué ocurre si necesito más operaciones, almacenamiento o salida?',
          a: 'Los planes pagados incluyen asignaciones mensuales. Las unidades de operación por encima de la asignación se suman a la próxima factura de la suscripción a la tarifa de excedentes y el almacenamiento por encima de ella se factura por GB-mes; el excedente de salida a clientes aún no se factura. Free detiene las nuevas escrituras al llegar a su asignación de operaciones y no tiene cargos por excedentes.',
        },
        {
          q: '¿Cómo se facturan las suscripciones?',
          a: 'Free no requiere tarjeta. Starter, Pro y Team se facturan mensual o anualmente; un año cuesta diez pagos mensuales, así que la facturación anual ahorra dos meses. No se ofrecen pruebas.',
        },
        {
          q: '¿Puedo exportar mis datos?',
          a: 'Sí. Lecturas, exportación, eliminación y reducción de uso siguen disponibles durante límites y períodos de lectura.',
        },
        {
          q: '¿Qué incluye Enterprise?',
          a: 'Enterprise tiene una base mensual de soporte de ${enterprisePriceUsd} más uso. Consulta las condiciones con ventas; Enterprise no se vende en el checkout.',
        },
      ],
      contactSales: 'Contactar ventas',
    },
    finalCta: {
      titleLead: '¿Listo para',
      titleAccent: 'empezar?',
      cta: 'Registrarse en Public Alpha',
    },
    cadence: {
      free: 'Sin tarjeta de pago',
      year: 'Facturación anual · equivalente a {monthly}/mes · {months} meses gratis',
      month: 'Facturación mensual',
      both: '{monthly}/mes o {annual}/año',
      contact: 'Soporte + uso · contactar ventas',
    },
    suffixUsage: '/mes + uso',
  },

  contact: {
    titleLead: 'Construyamos',
    titleAccent: 'algo juntos.',
  },

  about: {
    titleLead: 'Bases de datos SQL',
    titleAccent: 'en tres regiones.',
    ctaPricing: 'Ver precios',
    regionsAria: 'Regiones en servicio',
    missionTitle: 'Una plataforma para bases de datos, claves y facturación.',
    pillars: [
      {
        title: 'Fundación TerraBase',
        text: 'TerraScale se construye sobre TerraBase, que ejecuta SQL sobre Turso detrás de una única superficie pública de la plataforma.',
      },
      {
        title: 'Una región principal',
        text: 'Cada base de datos se almacena y se sirve en la región que eliges al crearla.',
      },
      {
        title: 'Facturación consciente del uso',
        text: 'Free no requiere tarjeta. Los planes pagados facturan en la próxima factura las unidades de operación por encima de la asignación y el almacenamiento por encima de ella se factura por GB-mes; el excedente de salida a clientes aún no se factura.',
      },
    ],
    bandTitle: 'Precios regionales planos, medidos por el uso real.',
    bandText:
      'El almacenamiento cuenta los bytes que cada base de datos guarda en su región principal. Distancia de red estimada y latencia medida de API siguen separadas.',
  },

  notFound: {
    title: 'No podemos encontrar esa página.',
    backHome: 'Volver al inicio',
  },
}
