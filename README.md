# TerraScale Landing Page

The official landing page for **TerraScale** - a Database-as-a-Service platform that provides enterprise-grade, scalable, and secure database solutions for modern applications.

## Tech Stack

- **React 19** - UI framework with the latest features
- **Vite 7** - Lightning-fast build tool and dev server
- **TypeScript** - Type-safe JavaScript
- **Tailwind CSS 4** - Utility-first CSS framework
- **React Router 7** - Client-side routing
- **Radix UI** - Accessible UI primitives (Accordion, Slot)
- **Lucide React** - Beautiful icon library
- **ShadCN UI** - Customizable component system

## Features

- Responsive design optimized for all devices
- Dark theme with earth-inspired color palette (#4379F2 blue, #6EC207 green)
- Smooth animations and transitions
- Interactive pricing page with billing toggle
- Contact forms and modals
- FAQ accordion sections
- SEO-friendly structure

## Getting Started

### Prerequisites

- [Bun](https://bun.sh/) (recommended) or Node.js 18+

### Installation

```bash
# Clone the repository
git clone https://github.com/your-org/TerraScale.LandingPage.git
cd TerraScale.LandingPage

# Install dependencies
bun install
```

### Development

```bash
# Start development server at http://localhost:5173
bun run dev

# Run linter
bun run lint

# Build for production
bun run build

# Preview production build at http://localhost:4173
bun run preview
```

## Project Structure

```
src/
├── components/          # Shared components
│   ├── ui/              # ShadCN UI components
│   │   ├── accordion.tsx
│   │   └── button.tsx
│   ├── Header.tsx       # Navigation header
│   ├── Footer.tsx       # Site footer
│   ├── ContactModal.tsx # Contact form modal
│   └── ScrollToTop.tsx  # Scroll restoration
├── pages/               # Route page components
│   ├── Home.tsx         # Landing page (/)
│   ├── About.tsx        # About page (/about)
│   ├── Pricing.tsx      # Pricing page (/pricing)
│   ├── Contact.tsx      # Contact page (/contact)
│   └── NotFound.tsx     # 404 page
├── lib/
│   └── utils.ts         # Utility functions (cn helper)
├── App.tsx              # Router configuration
├── main.tsx             # Application entry point
└── index.css            # Global styles & Tailwind
```

## Routes

| Path       | Component  | Description              |
|------------|------------|--------------------------|
| `/`        | Home       | Main landing page        |
| `/about`   | About      | Company information      |
| `/pricing` | Pricing    | Plans and pricing        |
| `/contact` | Contact    | Contact form             |
| `*`        | NotFound   | 404 error page           |

## Adding ShadCN Components

```bash
bunx shadcn@latest add <component> -y -o
```

## Path Aliases

The project uses `@/*` as an alias for `./src/*`:

```tsx
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
```

## Validation Workflow

Before committing changes:

1. Run linter and fix any errors:
   ```bash
   bun run lint
   ```

2. Verify production build succeeds:
   ```bash
   bun run build
   ```

3. Test all routes in development:
   ```bash
   bun run dev
   ```
   - Check `/`, `/about`, `/pricing`, `/contact`

## Environment

No environment variables are required for the landing page itself. External links point to:
- Documentation: `https://docs.terrascale.tech`
- Detailed pricing: `https://docs.terrascale.tech/pricing`

## Deployment

Build the production bundle:

```bash
bun run build
```

The output will be in the `dist/` directory, ready for deployment to any static hosting service (Vercel, Netlify, Cloudflare Pages, etc.).

## License

Proprietary - TerraScale
