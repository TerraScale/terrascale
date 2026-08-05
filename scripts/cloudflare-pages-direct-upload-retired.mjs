export const rejectDirectUpload = () => {
  throw new Error(
    'Cloudflare Pages Direct Upload is retired: Wrangler requires Pages Write, which cannot be safely constrained to deploy-only and can retarget or delete projects. Use the Git-integrated delivery design documented in README.md.',
  )
}
