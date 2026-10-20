import createClient from 'openapi-fetch'
import type { paths } from './schema'

/** Client for endpoints that need no access token: registration, sign-in and the session cookie endpoints. */
export const publicApi = createClient<paths>()
