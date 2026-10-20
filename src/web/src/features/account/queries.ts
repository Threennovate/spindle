import { queryOptions, useMutation, useQueryClient } from '@tanstack/react-query'
import { api } from '@/api/client'
import { publicApi } from '@/api/public'
import { toApiError } from '@/api/problem'
import type { components } from '@/api/schema'
import { unwrap } from '@/api/unwrap'
import { meQuery } from '@/auth/me'
import { createPasskey } from '@/auth/passkeys'
import { saveFile, suggestedFileName } from '@/lib/download'

type Schemas = components['schemas']

export const passkeysQuery = queryOptions({
  queryKey: ['me', 'passkeys'],
  queryFn: () => unwrap(api.GET('/api/me/passkeys')),
})

/** The Google and Apple logins of the account. */
export const externalLoginsQuery = queryOptions({
  queryKey: ['me', 'logins'],
  queryFn: () => unwrap(api.GET('/api/me/logins')),
})

export function useRemoveExternalLogin() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (provider: string) => unwrap(api.DELETE('/api/me/logins/{provider}', { params: { path: { provider } } })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: externalLoginsQuery.queryKey }),
  })
}

/** An account made with Google or Apple gets its first password from the same email as a forgotten one. */
export function useSendPasswordLink() {
  return useMutation({
    mutationFn: async (email: string) => {
      const { error, response } = await publicApi.POST('/api/auth/forgot-password', { body: { email } })
      if (!response.ok) {
        throw toApiError(response, error)
      }
    },
  })
}

export function useAddPasskey() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async ({ password, name }: { password?: string; name: string }) => {
      const { optionsJson } = await unwrap(api.POST('/api/me/passkeys/options', { body: { password } }))
      const credentialJson = await createPasskey(optionsJson)
      return unwrap(api.POST('/api/me/passkeys', { body: { credentialJson, name } }))
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: passkeysQuery.queryKey }),
  })
}

export function useRemovePasskey() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (credentialId: string) => unwrap(api.DELETE('/api/me/passkeys/{credentialId}', { params: { path: { credentialId } } })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: passkeysQuery.queryKey }),
  })
}

export const sessionsQuery = queryOptions({
  queryKey: ['me', 'sessions'],
  queryFn: () => unwrap(api.GET('/api/me/sessions')),
})

/** Saves to the account and shows the result at once everywhere, since the whole app reads it. */
function useMeMutation<TVariables>(mutationFn: (variables: TVariables) => Promise<Schemas['MeResponse']>) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn,
    onSuccess: (me) => queryClient.setQueryData(meQuery.queryKey, me),
  })
}

export function useUpdateProfile() {
  return useMeMutation((body: Schemas['ProfileRequest']) => unwrap(api.PUT('/api/me/profile', { body })))
}

export function useUpdatePreferences() {
  return useMeMutation((body: Schemas['PreferencesRequest']) => unwrap(api.PUT('/api/me/preferences', { body })))
}

export function useChangePassword() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (body: Schemas['ChangePasswordRequest']) => unwrap(api.POST('/api/me/password', { body })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: sessionsQuery.queryKey }),
  })
}

export function useRevokeSession() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (sessionId: string) => unwrap(api.DELETE('/api/me/sessions/{sessionId}', { params: { path: { sessionId } } })),
    onSettled: () => queryClient.invalidateQueries({ queryKey: sessionsQuery.queryKey }),
  })
}

export function useRevokeOtherSessions() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: () => unwrap(api.POST('/api/me/sessions/revoke-others')),
    onSettled: () => queryClient.invalidateQueries({ queryKey: sessionsQuery.queryKey }),
  })
}

export function useDeleteAccount() {
  return useMutation({ mutationFn: (password?: string) => unwrap(api.POST('/api/me/delete', { body: { password } })) })
}

/** Downloads everything the person tracked, as a JSON file. */
export async function downloadMyData(): Promise<void> {
  const { data, error, response } = await api.GET('/api/me/export', { parseAs: 'blob' })
  if (!response.ok || !data) {
    throw toApiError(response, error)
  }
  saveFile(data as unknown as Blob, suggestedFileName(response.headers.get('Content-Disposition')) ?? 'spindle-data.json')
}

/** Emails a new confirmation link; the server says 204 when the address is confirmed already. */
export function useResendConfirmation() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async () => {
      const { error, response } = await api.POST('/api/me/confirmation-email')
      if (!response.ok) {
        throw toApiError(response, error)
      }
      return response.status === 202 ? ('sent' as const) : ('confirmed' as const)
    },
    onSuccess: (result) => result === 'confirmed' && queryClient.invalidateQueries({ queryKey: meQuery.queryKey }),
  })
}
