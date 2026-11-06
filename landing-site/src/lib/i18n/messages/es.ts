import type { Messages } from './en'

export const es: Messages = {
  meta: {
    title: 'TerraScale Public Alpha | Base de datos SQL regional',
    description:
      'TerraScale Public Alpha: bases SQL con una región de escritura y réplicas de lectura cifradas en São Paulo, San Francisco Bay Area y Fráncfort.',
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
    getStarted: 'Crear cuenta',
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
    headline: 'Base de datos multirregión',
    cta: 'Crear cuenta',
  },


  features: {
    titleLead: 'Bases de datos SQL',
    titleAccent: 'y réplicas de lectura.',
    items: [
      { headline: 'Tablas SQL, índices y claves foráneas' },
      { headline: 'Conexiones con psql, HTTP y WebSocket' },
      { headline: 'Réplicas de lectura cifradas en otras regiones' },
      { headline: 'Shapes filtradas con permisos de lectura por tabla' },
      { headline: 'Bases para agentes con TTL, forks y claves limitadas' },
      { headline: 'El borrado destruye las claves de las copias de seguridad' },
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
        a: 'São Paulo (gru), San Francisco Bay Area (sjc) y Fráncfort (fra). Elige una región principal para escribir y otras regiones para réplicas de lectura cifradas.',
      },
      {
        q: '¿Mi base de datos se copia a otras regiones?',
        a: 'Sí. Elige regiones de réplica para mantener copias de solo lectura cifradas fuera de la región principal. REST, psql y WebSocket pueden leer una réplica local lista. Las réplicas tienen consistencia eventual. Las escrituras van a la región principal desde cualquier región en servicio.',
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
        a: 'El tráfico del cliente usa TLS y los archivos de base de datos, las réplicas y las copias de seguridad se cifran en reposo. El borrado destruye las claves de las copias de seguridad e impide restaurarlas. La limpieza física es un paso separado.',
      },
      {
        q: '¿Cuánto cuesta empezar?',
        a: 'Free no requiere tarjeta. Starter, Pro y Team se facturan mensual o anualmente. Las unidades de operación por encima de una asignación pagada van en la próxima factura y el almacenamiento por encima de ella se factura por GB-mes; el excedente de salida a clientes aún no se factura.',
      },
      {
        q: '¿Puedo exportar mis datos e irme?',
        a: 'Sí. Exporta desde el panel o la API en cualquier momento. No hay tarifa de salida por dejar TerraScale.',
      },
      { q: '¿Los clientes pueden suscribirse a parte de una tabla?', a: 'Sí. Las shapes en vivo admiten filtros y selección de columnas. Los permisos de lectura por tabla permiten a los tokens de usuario final leer las tablas autorizadas; un filtro no sustituye la autorización.' },
      { q: '¿Puedo dar a un agente su propia base de datos?', a: 'Sí. Crea una base con TTL, haz un fork y emite una clave limitada a una base con acceso de lectura o lectura y escritura. Al expirar, la base se elimina. La restauración a un punto en el tiempo en una base nueva está disponible; la restauración sobre la misma base llegará próximamente.' },
      { q: '¿Puedo limitar el uso?', a: 'Sí. Los planes aplican topes mensuales de escritura y uso razonable de lecturas SQL. El tope de escritura detiene nuevas escrituras; la asignación de lectura detiene lecturas SQL, pero las shapes siguen disponibles. Los topes de gasto definidos por el cliente llegarán próximamente.' },
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
    title: '¿Empezamos?',
    ctaPrimary: 'Crear cuenta',
  },

  pricingTeaser: {
    title: 'Empieza con Free. Compara las suscripciones.',
    cta: 'Comparar planes',
  },

  pricing: {
    recommended: 'Recomendado',
    titleLead: 'Precios',
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
      { title: 'Almacenamiento en la región principal' },
      { title: 'Escrituras contadas en unidades de 4 KiB' },
      { title: 'Topes de escritura por plan' },
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
      regionsPerDatabase: 'Regiones principales por base de datos',
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
          details: 'Cada 4 KiB escritos correctamente iniciados se cuentan por separado; cada fila modificada cuenta al menos una unidad.',
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
          a: 'Una unidad de escritura es cada 4 KiB (4.096 bytes) escritos correctamente, iniciados. Cada fila modificada cuenta al menos una unidad. Las lecturas SQL no se facturan y cuentan para el uso razonable mensual; las lecturas de shapes desde el edge son gratuitas. No se facturan solicitudes fallidas, denegadas, inválidas, limitadas o no encontradas.',
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
        { q: '¿Cómo se facturan las réplicas?', a: 'El catálogo incluye una región principal por base de datos en todos los planes. Las escrituras y los bytes almacenados se cuentan en la región principal; el almacenamiento de réplicas no tiene cargo separado. Se aplican los topes de escritura y el uso razonable de lecturas SQL.' },
      ],
      contactSales: 'Contactar ventas',
    },
    finalCta: {
      title: '¿Empezamos?',
      cta: 'Crear cuenta',
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
    eyebrow: 'Contacto TerraScale',
    titleLead: 'Construyamos',
    titleAccent: 'algo juntos.',
    subtitle:
      '¿Tienes una pregunta sobre tu proyecto, necesitas ayuda técnica o quieres hablar sobre una colaboración? Ponte en contacto con nuestro equipo.',
    emailCta: 'Escríbenos por correo',
    topicsTitle: '¿En qué podemos ayudarte?',
    topics: [
      {
        title: 'Soporte técnico',
        text: 'Preguntas sobre tu base de datos, la integración con el SDK o un error inesperado.',
      },
      {
        title: 'Planes y facturación',
        text: 'Ayuda para entender planes, facturas o qué opción se adapta mejor a tu proyecto.',
      },
      {
        title: 'Trabaja con nosotros',
        text: 'Colaboraciones, integraciones y otras formas de construir con TerraScale.',
      },
    ],
    github: {
      title: 'Encuéntranos en GitHub',
      text: 'Explora nuestros repositorios y SDKs.',
    },
    resourcesTitle: '¿Buscas una respuesta rápida?',
    resourcesSubtitle: 'Estos recursos son un buen punto de partida.',
    resources: {
      documentation: {
        title: 'Documentación',
        text: 'Guías y ejemplos para ayudarte a construir tu aplicación.',
      },
      status: {
        title: 'Estado de la plataforma',
        text: 'Consulta la disponibilidad y las actualizaciones sobre incidentes en curso.',
      },
      api: {
        title: 'Referencia de la API',
        text: 'Explora endpoints y formatos de solicitud para tu integración.',
      },
    },
    note: 'Para solicitudes de soporte técnico, incluye el mensaje de error y los pasos para reproducir el problema. Nunca compartas contraseñas ni claves de API.',
  },

  about: {
    titleLead: 'Bases de datos SQL',
    titleAccent: 'en tres regiones.',
    ctaPricing: 'Ver precios',
    regionsAria: 'Regiones en servicio',
    missionTitle: 'Una plataforma para bases de datos, claves y facturación.',
    pillars: [
      { title: 'Bases de datos SQL' },
      { title: 'Una región de escritura y réplicas cifradas' },
      { title: 'Topes de escritura por plan' },
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
