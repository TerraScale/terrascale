# TerraScale Landing Page

TerraScale Landing Page is a modern React Single Page Application (SPA) built with Vite, featuring a responsive design with Tailwind CSS. It serves as the marketing website for TerraScale's cloud platform, showcasing features, team information, pricing, and contact forms.

Always reference these instructions first and fallback to search or bash commands only when you encounter unexpected information that does not match the info here.

## Working Effectively

Bootstrap, build, and test the repository:
- Install dependencies: `npm install` -- takes 70 seconds to complete. NEVER CANCEL. Set timeout to 120+ seconds.
- Lint the code: `npm run lint` -- takes <1 second. Fix any lint errors before proceeding.
- Build for production: `npm run build` -- takes 5 seconds. NEVER CANCEL. Set timeout to 60+ seconds.
- Start development server: `npm run dev` -- starts in <1 second, serves on http://localhost:5173/
- Preview production build: `npm run preview` -- serves on http://localhost:4173/ after building

## Prerequisites

- Node.js v20+ is required (tested with v20.19.4)
- npm v10+ is required (tested with v10.8.2)

## Validation

**CRITICAL**: Always manually validate changes by running the complete application and testing user scenarios:

1. **Development Workflow Validation**:
   - Run `npm install && npm run lint && npm run build` first
   - Start dev server with `npm run dev`
   - Navigate to http://localhost:5173/ and verify all pages load
   - Test all navigation links: Home (/), About (/about), Pricing (/pricing), Contact (/contact)

3. **Navigation Testing**:
   - Click through all main navigation links
   - Verify active states are correctly highlighted
   - Test footer links and social media icons
   - Verify all "Get Started" and "View Pricing" CTAs work

4. **Build Validation**:
   - Always run `npm run build` after changes
   - Test the production preview with `npm run preview`
   - Verify the built application works identically to development

**ALWAYS run `npm run lint` before committing changes or the linting will fail.**

## Project Structure

The repository follows standard React + Vite conventions:

```
/
├── src/
│   ├── components/          # Reusable React components
│   │   ├── Header.jsx      # Main navigation header
│   │   └── Footer.jsx      # Site footer with links
│   ├── pages/              # Route-based page components
│   │   ├── Home.jsx        # Landing page with hero, features, testimonials
│   │   ├── About.jsx       # Company information and team
│   │   ├── Contact.jsx     # Contact form and business info
│   │   ├── Pricing.jsx     # Pricing plans and features
│   │   └── NotFound.jsx    # 404 error page
│   ├── lib/
│   │   └── utils.js        # Utility functions (cn class helper)
│   ├── assets/             # Static assets (images, icons)
│   ├── App.jsx             # Main app with React Router setup
│   ├── main.jsx            # React DOM entry point
│   └── index.css           # Global styles and Tailwind imports
├── public/                 # Static public assets
├── package.json            # Dependencies and npm scripts
├── vite.config.js          # Vite build configuration
├── tailwind.config.js      # Tailwind CSS customization
├── eslint.config.js        # ESLint rules and configuration
└── index.html              # Main HTML template
```

## Key Technologies

- **React**: Modern React with hooks and functional components
- **Vite**: Fast build tool and development server
- **React Router**: Client-side routing
- **Lucide React**: Icon library for UI elements
- **ESLint**: Code linting with React-specific rules
- **Tailwind 4**: Utility-first CSS framework
- **ShadCN**: Component library for building UI

## Common Tasks

### Adding New Pages
1. Create new component in `src/pages/`
2. Add route to `src/App.jsx` Routes component
3. Update navigation in `src/components/Header.jsx` if needed
4. Test navigation and routing functionality

### Working with Components
- All components are functional React components using hooks
- Header component manages mobile menu state and active navigation
- Contact form has full validation and submission handling
- Footer contains all site links and contact information

### Development Commands
- `npm run dev` - Start development server (http://localhost:5173/)
- `npm run build` - Build for production (outputs to `dist/`)
- `npm run preview` - Preview production build (http://localhost:4173/)
- `npm run lint` - Run ESLint on all source files

### Build Outputs
The production build creates:
- `dist/index.html` - Main HTML file (~0.46 kB)
- `dist/assets/index-[hash].css` - Compiled CSS (~24 kB, ~4.6 kB gzipped)
- `dist/assets/index-[hash].js` - Bundled JavaScript (~290 kB, ~89 kB gzipped)

## Troubleshooting

### Common Issues
- **Lint errors**: Run `npm run lint` to see errors. Most common: unused variables (fix by removing or prefixing with underscore)
- **Build failures**: Usually due to import errors or TypeScript-like syntax in .jsx files
- **Routing issues**: Ensure React Router setup in App.jsx matches your page structure
- **Style issues**: Verify Tailwind classes are correct and `cn()` utility is used for conditional styling

### Performance Notes
- Vite provides extremely fast hot reload in development
- Production builds are optimized and tree-shaken automatically
- Images and assets are optimized during build process

## Frequently Referenced Code Patterns

### Navigation Setup (from App.jsx):
```jsx
import { BrowserRouter as Router, Routes, Route } from 'react-router-dom'

function App() {
  return (
    <Router>
      <div className="min-h-screen bg-white">
        <Header />
        <main>
          <Routes>
            <Route path="/" element={<Home />} />
            <Route path="/about" element={<About />} />
            <Route path="/contact" element={<Contact />} />
            <Route path="/pricing" element={<Pricing />} />
            <Route path="*" element={<NotFound />} />
          </Routes>
        </main>
        <Footer />
      </div>
    </Router>
  )
}
```

### Utility Class Helper (from lib/utils.js):
```jsx
import { clsx } from "clsx"
import { twMerge } from "tailwind-merge"

export function cn(...inputs) {
  return twMerge(clsx(inputs))
}

// Usage: className={cn("base-classes", condition && "conditional-classes")}
```

## Testing Strategy

Since this project has no automated tests, manual testing is critical:

1. **Before making changes**: Test current functionality to establish baseline
2. **During development**: Use dev server for immediate feedback
3. **After changes**: Run full validation scenario described above
4. **Before committing**: Always run `npm run lint` and `npm run build`

## No CI/CD Pipeline

This repository currently has no GitHub Actions or automated testing. All validation must be done manually using the commands and scenarios described above.

# ShadCN
Use the following command `npx shadcn@latest add <component> -y -o` to add new components to the project