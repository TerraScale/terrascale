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
      security: 'Seguridad',
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
          privacy: 'Privacidad',
          terms: 'Términos',
          security: 'Seguridad',
          sla: 'SLA',
          compliance: 'Cumplimiento',
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
    highlights: ['Listo para IA', '99,99 % de Disponibilidad', '20 ms de Latencia'],
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
        text: 'Crea bases de datos, colecciones, esquemas, entidades y consultas SQL a través de una única API pública.',
        bullets: ['Planes de esquema', 'Explicar SQL', 'Consultas SDK'],
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
    headlineLead: 'Prueba TerraScale',
    headlineAccent: 'gratis durante 30 días.',
    text: 'Configúralo en minutos y obtén acceso total a todas las funciones de los planes Hobby y Pro. No se te cobrará nada hasta que decidas conservar el servicio.',
    ctaPrimary: 'Iniciar prueba gratis de 30 días',
    ctaSecondary: 'Hablar con ventas',
    perksAria: 'Detalles de la prueba',
    perks: ['Acceso a todas las funciones', 'Cancela cuando quieras', 'Sin permanencia'],
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
        description: 'Ideal para proyectos personales y prototipos',
        features: ['3 bases de datos', '1M de operaciones', '10 GB de almacenamiento', '15 GB de salida', 'Discord prioritario'],
        cta: 'Probar durante 30 días',
      },
      {
        description: 'Mejor para equipos en crecimiento y producción',
        features: [
          '15 bases de datos',
          '10M de operaciones',
          '50 GB de almacenamiento',
          '100 GB de salida',
          'Correo + Discord',
          'Soporte prioritario',
        ],
        cta: 'Probar durante 30 días',
      },
      {
        description: 'Para aplicaciones de misión crítica',
        features: ['Bases de datos ilimitadas', 'SLA personalizados', 'Soporte dedicado', 'Seguridad avanzada', 'Integraciones personalizadas'],
        cta: 'Contactar con ventas',
      },
    ],
    triad: [
      { title: 'Controles de región', text: 'Crea bases de datos en sfo, fra o gru' },
      { title: 'Observable por defecto', text: 'Trazas y registros de OpenTelemetry en los flujos principales' },
      { title: 'Protecciones de uso', text: 'Planes medidos con controles de facturación a nivel de plataforma' },
    ],
    detailed: {
      eyebrow: 'Precios detallados',
      titleLead: 'Precios claros para',
      titleAccent: 'despliegues personalizados',
      text: 'Los costos por uso, los alcances de soporte y los requisitos de despliegue se desglosan a continuación para que los equipos corporativos puedan estimar el gasto antes de hablar con ventas.',
      headItem: 'Concepto',
      headPricing: 'Precio',
      headDetails: 'Detalles',
      rows: [
        {
          item: 'Soporte nivel Enterprise',
          unit: '/mes',
          details: 'Acceso base a la plataforma para despliegues Enterprise y soporte avanzado.',
        },
        {
          item: 'Uso de almacenamiento',
          unit: '/GB',
          details: 'Precio de almacenamiento por uso además de la tarifa base de la plataforma.',
        },
        {
          item: 'Operaciones de base de datos',
          unit: '/ 1M ops',
          details: 'Escala con el volumen de trabajo para actividad de lectura, escritura y replicación.',
        },
      ],
      docsLink: 'Documentación completa de precios',
    },
    faq: {
      titleLead: 'Preguntas',
      titleAccent: 'frecuentes',
      subtitle: 'Respuestas a preguntas comunes sobre nuestros precios y planes.',
      items: [
        {
          q: '¿Qué es una Operación de Base de Datos?',
          a: 'Una Operación de Base de Datos es cualquier acción individual de lectura, escritura, actualización o eliminación sobre tu base de datos. Cada consulta que ejecutas — ya sea obtener un registro, insertar un dato, ejecutar una agregación o replicar a otra región — cuenta como una o más operaciones según su complejidad.',
        },
        {
          q: '¿Y si necesito más operaciones o almacenamiento?',
          a: 'Todos los planes admiten facturación por exceso a las tarifas de uso (1 USD por GB adicional de almacenamiento, 1 USD por cada millón de operaciones adicionales). Nunca se te cortará a mitad de una petición — te avisamos con antelación antes de acercarte a los límites para que puedas mejorar o ajustar tu plan.',
        },
        {
          q: '¿Cuándo puedo cambiar mi suscripción?',
          a: 'En cualquier momento. Las mejoras se aplican de inmediato con facturación prorrateada; las reducciones se aplican al inicio de tu próximo ciclo de facturación. Sin permanencia, sin cargos por cancelación.',
        },
        {
          q: '¿Puedo exportar mis datos e irme cuando quiera?',
          a: 'Sí — tus datos son tuyos. Las exportaciones completas están disponibles en cualquier momento a través del panel o la API, y nunca cobramos un centavo por sacarlos. La única restricción es un límite de tasa por petición, para que un trabajo de exportación descontrolado no desestabilice el clúster para otros inquilinos; no hay límite de volumen total ni tarifa de salida. Si TerraScale no te funciona, te vas con todo lo que aportaste.',
        },
        {
          q: '¿Ofrecen soluciones corporativas personalizadas?',
          a: 'Sí. Para equipos con requisitos específicos de cumplimiento, SLA personalizados o infraestructura dedicada, nuestro plan Enterprise es totalmente a medida. Contacta con ventas para dimensionar un despliegue que se ajuste a tus necesidades.',
        },
        {
          q: '¿Por qué exigen una tarjeta de crédito para la prueba gratuita?',
          a: 'Exigimos una tarjeta válida para evitar el abuso y asegurar un uso serio de nuestra infraestructura. No se te cobrará durante la prueba de 30 días y puedes cancelar en cualquier momento. Esto también hace que la transición a un plan de pago sea fluida si decides continuar.',
        },
      ],
      footerTitle: '¿Aún tienes preguntas?',
      footerText: 'Nuestro equipo está aquí para ayudarte a encontrar el plan perfecto para tus necesidades.',
      contactSales: 'Contactar con ventas',
      readDocs: 'Leer documentación',
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
