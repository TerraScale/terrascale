import { pathToFileURL } from 'node:url'
import { rejectDirectUpload } from './cloudflare-pages-direct-upload-retired.mjs'

export const runProjectCreate = () => rejectDirectUpload()

if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) {
  try {
    runProjectCreate()
  } catch (error) {
    console.error(error.message)
    process.exit(1)
  }
}
