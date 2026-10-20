import { queryOptions, useMutation } from '@tanstack/react-query'
import { api } from '@/api/client'
import { ApiError } from '@/api/problem'
import type { components } from '@/api/schema'
import { unwrap } from '@/api/unwrap'

export type AuthorizationRequest = components['schemas']['OAuthAuthorizationRequest']

/** The OAuth parameters an agent sent the browser with, as the authorization endpoint passed them on. */
export function readAuthorizationRequest(search: Record<string, unknown>): AuthorizationRequest {
  const text = (name: string) => (typeof search[name] === 'string' ? (search[name] as string) : undefined)
  return {
    responseType: text('response_type'),
    clientId: text('client_id'),
    redirectUri: text('redirect_uri'),
    codeChallenge: text('code_challenge'),
    codeChallengeMethod: text('code_challenge_method'),
    scope: text('scope'),
    state: text('state'),
    resource: text('resource'),
  }
}

/** Who asks to be let in, or why the request can't be answered. */
export const authorizationPreviewQuery = (request: AuthorizationRequest) =>
  queryOptions({
    queryKey: ['oauth', 'preview', request],
    queryFn: () => unwrap(api.POST('/api/oauth/authorizations/preview', { body: request })),
    retry: false,
    staleTime: Infinity,
  })

/** Where to send the person back to when the agent should hear what went wrong. */
export function errorRedirect(error: unknown): string | undefined {
  const url = error instanceof ApiError ? (error.problem as Record<string, unknown> | undefined)?.redirectUrl : undefined
  return typeof url === 'string' ? url : undefined
}

export function useAnswerAuthorization() {
  return useMutation({
    mutationFn: (body: components['schemas']['OAuthConsentRequest']) => unwrap(api.POST('/api/oauth/authorizations', { body })),
    // The answer goes back to the agent, which takes it from there.
    onSuccess: ({ redirectUrl }) => window.location.assign(redirectUrl),
  })
}
