import type { Messages } from './en'

export const es: Messages = {
  meta: {
    title: 'TerraScale Public Alpha | Base de Documentos Regional',
    description:
      'TerraScale Public Alpha ofrece una base de documentos tipada con replicación asíncrona en São Paulo, San Francisco Bay Area y Frankfurt.',
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
    tagline:
      'TerraScale Public Alpha ofrece una base de documentos regional sin porcentaje de disponibilidad ni SLA. La disponibilidad actual se publica en la página de estado.',
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
          community: 'Comunidad',
          support: 'Soporte',
          supportEmail: 'Contacto: support@terrascale.tech',
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
    eyebrow: 'Public Alpha',
    headlineLead: 'Base de datos de documentos tipada,',
    headlineAccent: 'replicada entre regiones.',
    subtitle:
      'Una base de documentos tipada con replicación asíncrona en São Paulo, San Francisco Bay Area y Frankfurt. El endpoint global dirige a la región seleccionada saludable más cercana. Public Alpha no tiene porcentaje de disponibilidad ni SLA.',
    ctaPrelaunch: 'Public Alpha',
    highlightsAria: 'Capacidades principales',
    highlights: ['Enrutamiento por región seleccionada', 'Replicación asíncrona', 'SDK tipados', 'Listo para agentes de IA'],
  },

  features: {
    eyebrow: 'Plataforma',
    titleLead: 'Public Alpha,',
    titleAccent: 'con límites explícitos',
    subtitle:
      'El contrato de lanzamiento cubre documentos, tres regiones activas, clientes tipados y salud visible sin compromiso de disponibilidad.',
    items: [
      {
        title: 'Datos',
        text: 'Las bases de datos contienen colecciones de documentos JSON, cada una con un esquema opcional. Consulta con filtros JSON o SQL de solo lectura: SELECT, WHERE, ORDER BY y hasta 1.000 filas por sentencia.',
        bullets: ['Colecciones', 'Filtros JSON', 'SQL de solo lectura'],
      },
      {
        title: 'Regiones',
        text: 'São Paulo, San Francisco Bay Area y Fráncfort ya sirven tráfico. Cada base de datos vive en una región principal y se replica completa en las demás regiones que selecciones.',
        bullets: ['gru', 'sjc', 'fra'],
      },
      {
        title: 'Enrutamiento',
        text: 'El endpoint global responde desde la región saludable más cercana que tenga tu base de datos. Los endpoints regionales estrictos nunca sirven desde una región que no seleccionaste.',
        bullets: ['Endpoint global', 'Endpoints regionales estrictos'],
      },
      {
        title: 'Salud',
        text: 'La replicación es asíncrona. Retraso, convergencia, conflictos y latencia de API se publican como valores medidos separados, con su hora de observación. No hay promesa de retraso limitado, porcentaje de disponibilidad ni SLA.',
        bullets: ['Replicación asíncrona', 'Hora de observación', 'Sin SLA'],
      },
      {
        title: 'Seguridad',
        text: 'El tráfico del cliente requiere TLS 1.2 o posterior, los datos en reposo usan cifrado calificado y cada solicitud requiere autorización. El contenido del cliente permanece en las regiones que selecciones.',
        bullets: ['TLS 1.2+', 'Cifrado en reposo', 'Autorización por solicitud'],
      },
    ],
  },

  code: {
    eyebrow: 'SDK para desarrolladores',
    headlineLead: 'Consulta tus datos',
    headlineAccent: 'en el lenguaje que amas',
    text: 'Los SDKs tipados de C# y TypeScript usan la misma API HTTP pública que el panel, con modelos generados a partir de su contrato OpenAPI.',
    bullets: ['Clientes tipados en C# y TypeScript', 'Modelos OpenAPI generados', 'Filtros JSON o SQL de solo lectura'],
    agent: {
      title: 'O pide a tu agente de IA favorito que lo haga por ti',
      text: 'Soportamos CLI y MCP. Conecta Claude Code, Cursor o cualquier cliente MCP al servidor MCP de TerraScale con un token de acceso personal. El agente actúa con tus roles y cada llamada se vuelve a verificar. Los agentes también pueden llamar a la API HTTP desde una terminal.',
      promptsLabel: 'Prueba a pedir',
      prompts: [
        'Lista mis bases de datos y las regiones que sirve cada una.',
        'Encuentra los clientes con 10 o más asientos en la base de datos app.',
        '¿Cuánto de la cuota de unidades de operación de este mes hemos usado?',
      ],
      toolsLabel: 'Algunas herramientas MCP',
    },
    copy: 'Copiar',
    copied: 'Copiado',
    copyAria: 'Copiar código',
    tablistLabel: 'Ejemplos de código',
  },

  cta: {
    headlineLead: 'Regístrate en',
    headlineAccent: 'TerraScale Public Alpha.',
    text: 'Free no requiere tarjeta. Los planes pagados facturan en la próxima factura las unidades de operación por encima de la asignación; los excedentes de almacenamiento y salida a clientes aún no se facturan.',
    ctaPrimary: 'Registrarse en Public Alpha',
    ctaSecondary: 'Hablar con ventas',
    perksAria: 'Detalles de planes',
    perks: ['Free sin tarjeta', 'Planes pagados mensuales o anuales'],
  },

  pricingTeaser: {
    eyebrow: 'Precios',
    title: 'Empieza con Free. Compara las suscripciones.',
    cta: 'Comparar planes',
  },

  pricing: {
    eyebrow: 'Precios',
    titleLead: 'Precios simples',
    titleAccent: 'que escalan contigo',
    subtitle: 'Empieza con Free o compra Starter, Pro o Team en el checkout. Enterprise se contrata con ventas.',
    badge: 'Más popular',
    suffixMonthly: '/mes',
    suffixAnnual: '/año',
    intervals: {
      label: 'Intervalo de facturación',
      month: 'Mensual',
      year: 'Anual',
      saving: '{months} meses gratis',
    },
    plans: {
      free: {
        description: 'Crea y evalúa sin tarjeta de pago',
        cta: 'Empezar con Free',
      },
      starter: {
        description: 'Suscripción para proyectos pequeños',
        cta: 'Elegir Starter',
      },
      pro: {
        description: 'Suscripción para proyectos en crecimiento',
        cta: 'Elegir Pro',
      },
      team: {
        description: 'Suscripción para equipos',
        cta: 'Elegir Team',
      },
      enterprise: {
        description: 'Soporte más uso, mediante ventas',
        features: ['Consultar condiciones con ventas', 'Límites y objetivo de respuesta de soporte a medida'],
        cta: 'Contactar ventas',
      },
    },
    limits: {
      projects: ['{n} proyecto', '{n} proyectos'],
      databases: ['{n} base de datos', '{n} bases de datos'],
      members: ['{n} miembro', '{n} miembros'],
      storage: '{n} entre regiones (promedio mensual)',
      operationUnits: '{n} unidades de operación/mes',
      egress: '{n} de salida a clientes/mes',
    },
    usage: {
      eyebrow: 'Cómo se mide el uso',
      title: 'Almacenamiento, operaciones y salida a clientes.',
    },
    triad: [
      {
        title: 'Almacenamiento entre regiones',
        text: 'GB decimales en cada región seleccionada, promediados durante el mes',
      },
      {
        title: 'Unidades de operación',
        text: 'Cada 4 KiB iniciados leídos o escritos; las escrituras cuentan una vez y la replicación es gratis',
      },
      {
        title: 'Asignaciones y uso adicional',
        text: 'Las unidades de operación sobre la asignación pagada van a la próxima factura; los excedentes de almacenamiento y salida a clientes aún no se facturan',
      },
    ],
    compare: {
      eyebrow: 'Comparar',
      title: 'Asignaciones por plan.',
      head: 'Asignación',
      projects: 'Proyectos',
      databases: 'Bases de datos',
      members: 'Miembros',
      storage: 'Almacenamiento entre regiones (promedio mensual)',
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
      eyebrow: 'Reglas de facturación',
      titleLead: 'Tarifas de uso',
      titleAccent: 'aprobadas',
      text: 'Los planes pagados incluyen las asignaciones. Las unidades de operación por encima de la asignación se suman a la próxima factura de la suscripción a esta tarifa; los excedentes de almacenamiento y salida a clientes aún no se facturan. Free detiene las nuevas escrituras al llegar a su asignación de operaciones y no tiene cargos por excedentes.',
      headItem: 'Item',
      headPricing: 'Precio',
      headDetails: 'Detalles',
      rows: [
        {
          item: 'Unidades de operación',
          unit: '/millón de unidades',
          details: 'Cada 4 KiB iniciados de cada lectura o escritura exitosa cuentan por separado. Las escrituras cuentan una vez, independientemente de las regiones seleccionadas; las lecturas cuentan en la región que las atiende. La replicación es gratis.',
        },
        {
          item: 'Almacenamiento entre regiones',
          unit: '/GB-región-mes',
          details: 'GB decimales almacenados en cada región seleccionada, promediados durante el mes.',
        },
        {
          item: 'Salida a clientes',
          unit: '/GB',
          details: 'Bytes decimales de respuestas de la aplicación enviados a clientes; se excluye el tráfico de replicación.',
        },
      ],
      docsLink: 'Documentación completa de precios',
    },
    faq: {
      eyebrow: 'Preguntas frecuentes',
      titleLead: 'Preguntas',
      titleAccent: 'frecuentes',
      subtitle: 'Respuestas directas sobre los planes de Public Alpha.',
      items: [
        {
          q: '¿Qué es una unidad de operación?',
          a: 'Una unidad de operación es cada 4 KiB (4.096 bytes) iniciados de un objeto lógico leído o escrito correctamente. Las listas, consultas y lotes cuentan cada elemento por separado. Las escrituras cuentan una vez, independientemente del número de regiones seleccionadas; las lecturas cuentan solo en la región que las atiende. La replicación entre regiones no se factura. Las solicitudes fallidas, denegadas, inválidas, limitadas y no encontradas no se facturan.',
        },
        {
          q: '¿Qué ocurre si necesito más operaciones, almacenamiento o salida?',
          a: 'Los planes pagados incluyen asignaciones mensuales. Las unidades de operación por encima de la asignación se suman a la próxima factura de la suscripción a la tarifa de excedentes; los excedentes de almacenamiento y salida a clientes aún no se facturan. Free detiene las nuevas escrituras al llegar a su asignación de operaciones y no tiene cargos por excedentes.',
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
      footerTitle: '¿Todavía tienes dudas?',
      footerText: 'Contáctanos sobre planes, límites o condiciones de Enterprise.',
      contactSales: 'Contactar ventas',
      readDocs: 'Leer documentación',
    },
    finalCta: {
      titleLead: '¿Listo para',
      titleAccent: 'empezar?',
      text: 'Empieza con Free o elige un plan pagado en el checkout.',
      cta: 'Registrarse en Public Alpha',
    },
    status: 'Los planes pagados están disponibles en el checkout. Las unidades de operación por encima de la asignación se suman a la próxima factura; los excedentes de almacenamiento y salida a clientes aún no se facturan.',
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
    eyebrow: 'Ponte en contacto',
    titleLead: 'Construyamos',
    titleAccent: 'algo juntos.',
    subtitle:
      '¿Tienes preguntas? ¿Necesitas un plan personalizado? ¿Quieres integrarte con TerraScale? Nos encantaría saber de ti.',
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
    success: '¡Gracias! Tu mensaje se ha enviado de forma segura a nuestro equipo.',
    errorNoForm: 'No se pudo enviar el formulario. Escríbenos directamente por correo.',
    errorSend: 'Algo salió mal al enviar tu mensaje. Inténtalo de nuevo o escríbenos directamente por correo.',
    note: 'El tráfico del cliente requiere TLS 1.2 o posterior. Los datos en reposo usan cifrado calificado por el servicio.',
    channelsTitle: 'Contáctanos',
    channels: [
      { value: 'support@terrascale.tech' },
      { value: 'Únete a nuestra comunidad' },
      { value: 'Abrir issues y PRs' },
    ],
  },

  about: {
    eyebrow: 'Acerca de TerraScale',
    titleLead: 'Infraestructura de base de datos',
    titleAccent: 'distribuida globalmente.',
    subtitle:
      'TerraScale Public Alpha ofrece una base de documentos en São Paulo, San Francisco Bay Area y Frankfurt con replicación asíncrona.',
    ctaPricing: 'Ver precios',
    ctaContact: 'Habla con nosotros',
    missionEyebrow: 'Misión',
    missionTitle: 'Una única superficie de plataforma para equipos que entregan entre regiones.',
    missionText:
      'Los proyectos eligen sus regiones. El endpoint global usa la región seleccionada saludable más cercana. Los endpoints regionales estrictos no sirven desde otra región.',
    pillars: [
      {
        title: 'Fundación TerraBase',
        text: 'TerraScale se construye sobre TerraBase para que los equipos ejecuten flujos de datos duraderos de documentos, consultas y operaciones detrás de una única superficie pública de la plataforma.',
      },
      {
        title: 'Replicación asíncrona',
        text: 'Una escritura exitosa confirma aceptación local. La replicación a otras regiones seleccionadas ocurre después.',
      },
      {
        title: 'Facturación consciente del uso',
        text: 'Free no requiere tarjeta. Los planes pagados facturan en la próxima factura las unidades de operación por encima de la asignación; los excedentes de almacenamiento y salida a clientes aún no se facturan.',
      },
    ],
    pricingPrincipleEyebrow: 'Principio de precios',
    bandTitle: 'Precios regionales planos, medidos por el uso real.',
    bandText:
      'El almacenamiento suma los bytes en cada región seleccionada. Distancia de red estimada, latencia medida de API y salud de replicación siguen separadas.',
  },

  notFound: {
    eyebrow: 'Página no encontrada',
    title: 'No podemos encontrar esa página.',
    text: 'La página que buscas se ha movido o nunca existió. Vamos a ponerte de vuelta en camino.',
    backHome: 'Volver al inicio',
    contactSupport: 'Contactar con soporte',
  },
}
