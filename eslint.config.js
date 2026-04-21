import svelteConfig from './svelte.config.js'
import js from '@eslint/js'
import globals from 'globals'
import svelte from 'eslint-plugin-svelte'
import tseslint from 'typescript-eslint'

export default tseslint.config(
  {
    ignores: [
      '.svelte-kit',
      'build',
      'dist',
      '.wrangler',
      'tmp',
      'src/**/*.tsx',
      'src/**/*.jsx',
      'src/components/motion/**/*',
      'src/components/ui/**/*',
      'src/lib/utils.ts',
    ],
  },
  js.configs.recommended,
  ...tseslint.configs.recommended,
  ...svelte.configs.recommended,
  {
    languageOptions: {
      globals: globals.browser,
    },
  },
  {
    files: ['**/*.svelte', '**/*.svelte.ts', '**/*.svelte.js'],
    languageOptions: {
      parserOptions: {
        projectService: true,
        extraFileExtensions: ['.svelte'],
        parser: tseslint.parser,
        svelteConfig,
      },
    },
  },
  {
    rules: {
      'no-undef': 'off',
      '@typescript-eslint/no-unused-vars': ['error', { varsIgnorePattern: '^[A-Z_]' }],
    },
  },
  {
    files: ['src/components/ExternalLink.svelte', 'src/components/RouteLink.svelte'],
    rules: {
      'svelte/no-navigation-without-resolve': 'off',
    },
  },
  {
    files: ['src/pages/Home.svelte'],
    rules: {
      'svelte/no-at-html-tags': 'off',
    },
  },
)
