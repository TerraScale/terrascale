import type { Messages } from './en'

export const es: Messages = {
  meta: {
    title: 'TerraScale Public Alpha | Base de Documentos Regional',
    description:
      'TerraScale Public Alpha ofrece una base de documentos tipada con replicación asíncrona en São Paulo, San Francisco y Falkenstein.',
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
    getStarted: 'Empezar',
    prelaunch: 'Public Alpha el 7 de septiembre',
    toggleNav: 'Abrir menú de navegación',
    language: {
      trigger: 'Idioma',
      currentSuffix: 'Idioma: {label}',
      menu: 'Selecciona un idioma',
    },
  },

  footer: {
    tagline:
      'TerraScale Public Alpha ofrece una base de documentos regional sin porcentaje de disponibilidad ni SLA.',
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
        },
      },
      resources: {
        title: 'Recursos',
        links: {
          docs: 'Docs',
          apiReference: 'Referencia de la API',
          status: 'Estado',
          community: 'Comunidad',
          security: 'Seguridad',
          support: 'Soporte',
        },
      },
      legal: {
        title: 'Legal',
        links: {
          terms: 'Términos',
          privacy: 'Privacidad',
          acceptableUse: 'Uso aceptable',
          dataProcessingTerms: 'Términos de tratamiento de datos',
          support: 'Soporte',
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
    headlineLead: 'TerraScale',
    headlineAccent: 'Public Alpha',
    subtitle:
      'Una base de documentos tipada con replicación asíncrona en São Paulo, San Francisco y Falkenstein. El endpoint global dirige a la región seleccionada saludable más cercana. Public Alpha no tiene porcentaje de disponibilidad ni SLA.',
    ctaPrimary: 'Registrarse en Public Alpha',
    ctaPrelaunch: 'Public Alpha abre el 7 de septiembre',
    ctaSecondary: 'Ver documentación',
    highlightsAria: 'Capacidades principales',
    highlights: ['Enrutamiento por región seleccionada', 'Replicación asíncrona', 'SDK tipados'],
  },

  features: {
    eyebrow: 'Plataforma',
    titleLead: 'Public Alpha,',
    titleAccent: 'con límites explícitos',
    subtitle:
      'El contrato de lanzamiento cubre documentos, tres regiones seleccionables, clientes tipados y salud visible sin compromiso de disponibilidad.',
    items: [
      {
        title: 'API de datos tipados',
        text: 'Crea bases de datos, repositorios, esquemas y documentos, y consulta con TQL a través de una única API pública.',
        bullets: ['Repositorios', 'TQL', 'Clientes SDK'],
      },
      {
        title: 'Enrutamiento regional',
        text: 'El endpoint global elige la región seleccionada saludable más cercana. Los endpoints regionales estrictos nunca sirven desde otra región.',
        bullets: ['Endpoint global', 'Endpoints regionales estrictos', 'Regiones seleccionadas'],
      },
      {
        title: 'Regiones de lanzamiento',
        text: 'Elige São Paulo, San Francisco o Falkenstein durante Public Alpha.',
        bullets: ['sfo', 'fra', 'gru'],
      },
      {
        title: 'Señales medidas',
        text: 'La salud de replicación, convergencia, conflictos, latencia de API y estimaciones geográficas siguen siendo valores separados. El contenido del cliente permanece en las regiones seleccionadas. El tráfico del cliente requiere TLS 1.2 o posterior, los datos en reposo usan cifrado calificado y cada solicitud aún requiere autorización.',
        bullets: ['Hora de observación', 'Etiquetas de evidencia', 'Sin promesa de retraso limitado'],
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
      'Entradas de región para gru, sfo, fra',
    ],
    copy: 'Copiar',
    copied: 'Copiado',
    copyAria: 'Copiar código',
    tablistLabel: 'Ejemplos de lenguaje',
  },

  cta: {
    headlineLead: 'Regístrate en',
    headlineAccent: 'TerraScale Public Alpha.',
    text: 'Starter y Pro ofrecen una prueba elegible de 30 días por método de pago verificado y organización.',
    ctaPrimary: 'Registrarse en Public Alpha',
    ctaPrelaunch: 'Public Alpha abre el 7 de septiembre',
    ctaSecondary: 'Hablar con ventas',
    perksAria: 'Detalles de planes',
    perks: ['Planes pagados mensuales', 'Sin excedentes pagados'],
    policyLead: 'Revisa antes de verificar el pago:',
  },

  pricing: {
    eyebrow: 'Precios',
    titleLead: 'Precios simples',
    titleAccent: 'que escalan contigo',
    subtitle: 'Tres planes para Public Alpha. Enterprise sigue sin estar disponible.',
    badge: 'Más popular',
    toggleLabel: 'Ciclo de facturación',
    monthly: 'Mensual',
    annual: 'Anual',
    save: 'Dos meses gratis',
    suffixMonthly: '/mes',
    suffixAnnual: '/año',
    plans: {
      starter: {
        description: 'Plan mensual con una prueba elegible de 30 días',
        features: ['5 proyectos', '10 bases de datos', '5 miembros', '5 GB entre regiones', '1.000.000 unidades de operación/mes'],
        cta: 'Elegir Starter',
      },
      pro: {
        description: 'Plan mensual con una prueba elegible de 30 días',
        features: ['25 proyectos', '50 bases de datos', '25 miembros', '50 GB entre regiones', '10.000.000 unidades de operación/mes'],
        cta: 'Elegir Pro',
      },
      enterprise: {
        description: 'Plan contractual futuro',
        features: ['Base mensual de soporte más uso medido', 'No disponible durante Public Alpha'],
        cta: 'Contacto para acceso futuro',
      },
    },
    triad: [
      { title: 'Almacenamiento entre regiones', text: 'Los bytes cuentan una vez en cada región seleccionada' },
      { title: 'Unidades de operación', text: 'Cada 4 KiB iniciados de documento cuentan como una unidad' },
      { title: 'Límites estrictos', text: 'Lecturas, exportaciones, eliminación y reducción de uso siguen disponibles' },
    ],
    detailed: {
      eyebrow: 'Reglas de facturación',
      titleLead: 'Public Alpha',
      titleAccent: 'sin excedentes pagados',
      text: 'El almacenamiento suma los bytes en cada región seleccionada. Las unidades de operación redondean cada documento por separado.',
      headItem: 'Item',
      headPricing: 'Precio',
      headDetails: 'Detalles',
      rows: [
        {
          item: 'Asignaciones de planes',
          unit: '',
          details: 'Cada plan incluye sus asignaciones publicadas de almacenamiento entre regiones y unidades mensuales de operación.',
        },
        {
          item: 'Comportamiento al límite',
          unit: '',
          details: 'El trabajo facturable nuevo se detiene. Lecturas, exportaciones, eliminación y reducción de uso siguen disponibles.',
        },
        {
          item: 'Base de soporte Enterprise',
          unit: '/mes + uso',
          details: 'Enterprise no está disponible durante Public Alpha y no tiene acción de compra.',
        },
      ],
      docsLink: 'Documentación completa de precios',
    },
    faq: {
      titleLead: 'Preguntas',
      titleAccent: 'frecuentes',
      subtitle: 'Respuestas directas sobre los planes de Public Alpha.',
      items: [
        {
          q: '¿Qué es una unidad de operación?',
          a: 'Cada 4 KiB iniciados de datos de documento sin comprimir cuentan como una unidad. Las escrituras cuentan en cada región seleccionada. Los documentos en lote se redondean por separado.',
        },
        {
          q: '¿Qué ocurre al alcanzar un límite?',
          a: 'Public Alpha usa límites estrictos, no excedentes pagados. Lecturas, exportaciones, eliminación y reducción de uso siguen disponibles.',
        },
        {
          q: '¿Cuándo puedo cambiar de plan?',
          a: 'Los propietarios de organización pueden cambiar planes mensuales en el Dashboard. Los planes pagados renuevan mensualmente. La cancelación inicia 30 días de solo lectura si el uso supera el plan.',
        },
        {
          q: '¿Puedo exportar mis datos?',
          a: 'Sí. Lecturas, exportación, eliminación y reducción de uso siguen disponibles durante límites y períodos de lectura.',
        },
        {
          q: '¿Qué incluye el plan Enterprise?',
          a: 'Enterprise tiene una base mensual de soporte de US$ 500 más uso medido, pero no está disponible durante Public Alpha.',
        },
      ],
      footerTitle: '¿Todavía tienes dudas?',
      footerText: 'Starter responde en dos días hábiles. Pro responde en un día hábil.',
      contactSales: 'Contactar ventas',
      readDocs: 'Leer documentación',
    },
    finalCta: {
      titleLead: '¿Listo para',
      titleAccent: 'empezar?',
      text: 'Regístrate mediante el flujo canónico del Dashboard TerraScale.',
      cta: 'Registrarse en Public Alpha',
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
    success: '¡Gracias! Tu mensaje se ha enviado de forma segura a nuestro equipo.',
    errorNoForm: 'No se pudo enviar el formulario. Escríbenos directamente por correo.',
    errorSend: 'Algo salió mal al enviar tu mensaje. Inténtalo de nuevo o escríbenos directamente por correo.',
    note: 'El tráfico del cliente requiere TLS 1.2 o posterior. Los datos en reposo usan cifrado calificado por el servicio.',
    channelsTitle: 'Otras formas de contactarnos',
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
      'TerraScale Public Alpha ofrece una base de documentos en São Paulo, San Francisco y Falkenstein con replicación asíncrona.',
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
        text: 'Los planes usan asignaciones explícitas de almacenamiento entre regiones y unidades de operación, sin excedentes pagados durante Public Alpha.',
      },
    ],
    pricingPrincipleEyebrow: 'Principio de precios',
    bandTitle: 'Precios regionales planos, medidos por el uso real.',
    bandText:
      'El almacenamiento suma los bytes en cada región seleccionada. Distancia de red estimada, latencia medida de API y salud de replicación siguen separadas.',
    regions: ['São Paulo', 'San Francisco', 'Falkenstein'],
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
