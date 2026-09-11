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
      '.terrascale-develop-release',
      'tmp',
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
    files: ['*.js', '*.ts', 'scripts/**/*.{js,mjs,cjs,ts}', 'tests/**/*.{js,mjs,cjs,ts}'],
    languageOptions: {
      globals: globals.node,
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
      '@typescript-eslint/no-unused-vars': ['error', { varsIgnorePattern: '^[A-Z_]' }],
    },
  },
)
