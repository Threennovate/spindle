import { useQuery } from '@tanstack/react-query'
import { Bot, Laptop } from 'lucide-react'
import { useState, type FormEvent, type ReactNode } from 'react'
import { toast } from 'sonner'
import { ApiError } from '@/api/problem'
import type { Me, Preferences } from '@/api/types'
import { CONNECTION_ERROR, hasErrors, toFormErrors, type FormErrors } from '@/auth/form-errors'
import { passkeyError, supportsPasskeys } from '@/auth/passkeys'
import { PageHeader } from '@/components/page-header'
import { TextField } from '@/components/text-field'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { Switch } from '@/components/ui/switch'
import { ProviderLogo } from '@/components/external-sign-in'
import { serverQuery } from '@/features/subscriptions/queries'
import { errorMessage } from '@/lib/error-message'
import { formatDurationAs, type DurationFormat } from '@/lib/time/duration'
import { browserTimeZone, dayKey, formatClock, formatDayAs } from '@/lib/time/zone'
import { usePageTitle } from '@/lib/title'
import { useUpdateAgentActivitySettings } from '../agent-activity/queries'
import { useWorkspace } from '../workspace'
import {
  externalLoginsQuery,
  passkeysQuery,
  useAddPasskey,
  useRemoveExternalLogin,
  useSendPasswordLink,
  useRemovePasskey,
  sessionsQuery,
  useChangePassword,
  useResendConfirmation,
  useRevokeOtherSessions,
  useRevokeSession,
  useUpdatePreferences,
  useUpdateProfile,
} from './queries'
import { DeleteAccountButton, DownloadDataButton, SignInAgain } from './account-actions'
import { TimeZonePicker } from './time-zone-picker'

const WEEKDAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday']
const DATE_FORMATS = ['d MMM yyyy', 'dd.MM.yyyy', 'dd/MM/yyyy', 'MM/dd/yyyy', 'yyyy-MM-dd']
const DURATION_FORMATS: DurationFormat[] = ['hms', 'hm', 'decimal']
const EXAMPLE_DAY = '2026-09-28'

/** The signed-in person's own settings: profile, how time is shown, password, devices and data. */
export function AccountPage() {
  usePageTitle('Account')
  const { me } = useWorkspace()

  return (
    <div className="mx-auto grid max-w-3xl gap-6">
      <PageHeader title="Account" description="Your profile, how times are shown to you, and where you are signed in." />
      <ProfileSection key={`${me.fullName}|${me.timeZone}`} me={me} />
      <PreferencesSection key={JSON.stringify(me.preferences)} preferences={me.preferences} />
      <PomodoroSection key={`pomodoro-${JSON.stringify(me.preferences)}`} preferences={me.preferences} />
      {me.hasPassword === false ? <SetPasswordSection email={me.email} /> : <PasswordSection />}
      <SignInMethodsSection email={me.email} />
      <PasskeysSection passwordless={me.hasPassword === false} />
      <SessionsSection />
      <AgentActivitySection me={me} />
      <DataSection />
      <DeleteSection />
    </div>
  )
}

function PasskeysSection({ passwordless }: { passwordless: boolean }) {
  const passkeys = useQuery(passkeysQuery)
  const add = useAddPasskey()
  const remove = useRemovePasskey()
  const [adding, setAdding] = useState(false)
  const [removing, setRemoving] = useState<{ id: string; name: string } | null>(null)
  const [name, setName] = useState('')
  const [password, setPassword] = useState('')
  const [errors, setErrors] = useState<FormErrors<'name' | 'password'>>({})

  async function handleAdd(event: FormEvent) {
    event.preventDefault()
    const validation = {
      name: !name.trim() ? 'Enter a name for this passkey.' : undefined,
      password: !passwordless && !password ? 'Enter your password to confirm.' : undefined,
    }
    setErrors(validation)
    if (hasErrors(validation)) return
    try {
      await add.mutateAsync({ name: name.trim(), password: passwordless ? undefined : password })
      setAdding(false)
      setPassword('')
      toast.success('Passkey added')
    } catch (error) {
      setErrors(error instanceof ApiError && error.status === 400
        ? toFormErrors(error, ['name', 'password']) : { form: passkeyError(error) })
    }
  }

  return (
    <Section
      title="Passkeys"
      description={
        passwordless
          ? 'Sign in with your fingerprint, face, device PIN or security key.'
          : 'Sign in with your fingerprint, face, device PIN or security key. Your password remains available for account recovery.'
      }
    >
      <div className="grid gap-3">
        {passkeys.isPending && <Skeleton className="h-14 w-full" />}
        {passkeys.isError && <p role="alert" className="text-muted-foreground text-sm">{errorMessage(passkeys.error, "Couldn't load your passkeys.")}</p>}
        {passkeys.data?.length === 0 && <p className="text-muted-foreground text-sm">You haven't added any passkeys yet.</p>}
        {!!passkeys.data?.length && (
          <ul aria-label="Your passkeys" className="divide-y rounded-lg border">
            {passkeys.data.map((passkey) => (
              <li key={passkey.id} className="flex items-center gap-3 px-3 py-2.5">
                <div className="min-w-0 flex-1">
                  <p className="truncate text-sm font-medium">{passkey.name}</p>
                  <p className="text-muted-foreground text-xs">Added {new Date(passkey.createdAt).toLocaleDateString()}</p>
                </div>
                <Button variant="outline" size="sm" disabled={remove.isPending} onClick={() => setRemoving(passkey)}>Remove</Button>
              </li>
            ))}
          </ul>
        )}
        {supportsPasskeys() ? (
          <div><Button variant="outline" disabled={add.isPending || (passkeys.data?.length ?? 0) >= 10} onClick={() => {
            setName('')
            setPassword('')
            setErrors({})
            setAdding(true)
          }}>Add a passkey</Button></div>
        ) : <p className="text-muted-foreground text-sm">To add a passkey, open Spindle in a current browser over HTTPS or on localhost.</p>}
      </div>
      <Dialog open={adding} onOpenChange={(open) => { if (!add.isPending) { setAdding(open); setPassword('') } }}>
        <DialogContent className="sm:max-w-md">
          <form noValidate onSubmit={handleAdd} className="grid gap-5">
            <DialogHeader>
              <DialogTitle>Add a passkey</DialogTitle>
              <DialogDescription>{passwordless ? 'Name it so you can recognize it later.' : 'Name it so you can recognize it later, then confirm your password.'}</DialogDescription>
            </DialogHeader>
            {errors.form && <Alert variant="destructive"><AlertDescription>{errors.form}</AlertDescription></Alert>}
            <TextField id="passkey-name" label="Passkey name" value={name} onChange={(e) => setName(e.target.value)} maxLength={100} autoFocus error={errors.name} />
            {passwordless ? (
              errors.password && <SignInAgain message={errors.password} />
            ) : (
              <TextField id="passkey-password" label="Your password" type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} error={errors.password} />
            )}
            <DialogFooter>
              <Button type="button" variant="ghost" disabled={add.isPending} onClick={() => { setAdding(false); setPassword('') }}>Cancel</Button>
              <Button type="submit" disabled={add.isPending}>{add.isPending ? 'Waiting for your authenticator…' : 'Create passkey'}</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>
      <Dialog open={removing !== null} onOpenChange={(open) => { if (!open && !remove.isPending) setRemoving(null) }}>
        <DialogContent className="sm:max-w-md">
          <DialogHeader>
            <DialogTitle>Remove passkey?</DialogTitle>
            <DialogDescription>You will no longer be able to sign in with {removing?.name}. You can still use your password or another passkey.</DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button variant="ghost" disabled={remove.isPending} onClick={() => setRemoving(null)}>Cancel</Button>
            <Button variant="destructive" disabled={remove.isPending} onClick={() => {
              if (removing) remove.mutate(removing.id, {
                onSuccess: () => { setRemoving(null); toast.success('Passkey removed') },
                onError: (error) => toast.error(errorMessage(error, "Couldn't remove the passkey.")),
              })
            }}>Remove passkey</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </Section>
  )
}

function Section({ title, description, children, destructive = false }: { title: string; description: string; children: ReactNode; destructive?: boolean }) {
  return (
    <Card className={destructive ? 'border-destructive/40' : undefined}>
      <CardHeader>
        <CardTitle className={destructive ? 'text-destructive' : undefined}>{title}</CardTitle>
        <CardDescription>{description}</CardDescription>
      </CardHeader>
      <CardContent>{children}</CardContent>
    </Card>
  )
}

function ProfileSection({ me }: { me: Me }) {
  const update = useUpdateProfile()
  const [fullName, setFullName] = useState(me.fullName)
  const [timeZone, setTimeZone] = useState(me.timeZone)
  const [errors, setErrors] = useState<FormErrors<'fullName' | 'timeZone'>>({})
  const deviceZone = browserTimeZone()

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    if (!fullName.trim()) {
      setErrors({ fullName: 'Enter your name.' })
      return
    }
    try {
      await update.mutateAsync({ fullName: fullName.trim(), timeZone })
      toast.success('Profile saved')
    } catch (error) {
      setErrors(error instanceof ApiError ? toFormErrors(error, ['fullName', 'timeZone'] as const) : { form: CONNECTION_ERROR })
    }
  }

  return (
    <Section title="Profile" description="Your name is how teammates see you. Days and times are counted in your time zone.">
      <form noValidate onSubmit={handleSubmit} className="grid gap-4">
        {errors.form && (
          <Alert variant="destructive">
            <AlertDescription>{errors.form}</AlertDescription>
          </Alert>
        )}
        <TextField id="account-name" label="Full name" value={fullName} onChange={(e) => setFullName(e.target.value)} error={errors.fullName} autoComplete="name" />
        <div className="grid gap-2">
          <Label htmlFor="account-email">Email</Label>
          <div className="flex items-center gap-2">
            <Input id="account-email" value={me.email} readOnly className="bg-muted/40 sm:w-80" />
            {me.emailConfirmed ? <Badge variant="secondary">Confirmed</Badge> : <Badge variant="outline">Not confirmed</Badge>}
            {!me.emailConfirmed && <ResendConfirmation email={me.email} />}
          </div>
        </div>
        <div className="grid gap-2">
          <Label htmlFor="account-zone">Time zone</Label>
          <div className="flex flex-wrap items-center gap-2">
            <TimeZonePicker id="account-zone" value={timeZone} onChange={setTimeZone} />
            {deviceZone !== timeZone && (
              <Button type="button" variant="link" size="sm" onClick={() => setTimeZone(deviceZone)}>
                Use this device's ({deviceZone})
              </Button>
            )}
          </div>
          {errors.timeZone && <p className="text-destructive text-sm">{errors.timeZone}</p>}
        </div>
        <div>
          <Button type="submit" disabled={update.isPending || (fullName.trim() === me.fullName && timeZone === me.timeZone)}>
            Save profile
          </Button>
        </div>
      </form>
    </Section>
  )
}

function ResendConfirmation({ email }: { email: string }) {
  const resend = useResendConfirmation()
  return (
    <Button
      type="button"
      variant="link"
      size="sm"
      disabled={resend.isPending || resend.isSuccess}
      onClick={() =>
        resend.mutate(undefined, {
          onSuccess: (result) => toast.success(result === 'sent' ? `We sent a new link to ${email}` : 'Your email is confirmed already'),
          onError: (error) => toast.error(errorMessage(error, "Couldn't send the email.")),
        })
      }
    >
      {resend.isSuccess ? 'Link sent' : 'Send the confirmation link again'}
    </Button>
  )
}

function PreferencesSection({ preferences }: { preferences: Preferences }) {
  const update = useUpdatePreferences()
  const [values, setValues] = useState(preferences)
  const changed = JSON.stringify(values) !== JSON.stringify(preferences)

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    try {
      await update.mutateAsync(values)
      toast.success('Preferences saved')
    } catch (error) {
      toast.error(errorMessage(error, "Couldn't save your preferences."))
    }
  }

  const set = <K extends keyof Preferences>(key: K, value: Preferences[K]) => setValues((current) => ({ ...current, [key]: value }))

  return (
    <Section title="Preferences" description="How dates, times and durations are shown to you, on every device.">
      <form onSubmit={handleSubmit} className="grid gap-4 sm:grid-cols-2">
        <Choice id="pref-week" label="First day of the week" value={String(values.weekStart)} onChange={(v) => set('weekStart', Number(v))} options={WEEKDAYS.map((day, index) => [String(index), day])} />
        <Choice id="pref-date" label="Date format" value={values.dateFormat} onChange={(v) => set('dateFormat', v)} options={DATE_FORMATS.map((f) => [f, formatDayAs(EXAMPLE_DAY, f)])} />
        <Choice
          id="pref-time"
          label="Time format"
          value={values.timeFormat}
          onChange={(v) => set('timeFormat', v)}
          options={[
            ['24h', '24-hour (13:30)'],
            ['12h', '12-hour (1:30 PM)'],
          ]}
        />
        <Choice
          id="pref-duration"
          label="Duration format"
          value={values.durationFormat}
          onChange={(v) => set('durationFormat', v)}
          options={DURATION_FORMATS.map((f) => [f, formatDurationAs(5400, f)])}
        />
        <div className="sm:col-span-2">
          <Button type="submit" disabled={!changed || update.isPending}>
            Save preferences
          </Button>
        </div>
      </form>
    </Section>
  )
}

function Choice({ id, label, value, onChange, options }: { id: string; label: string; value: string; onChange: (value: string) => void; options: [string, string][] }) {
  return (
    <div className="grid gap-2">
      <Label htmlFor={id}>{label}</Label>
      <Select value={value} onValueChange={onChange}>
        <SelectTrigger id={id} className="w-full">
          <SelectValue />
        </SelectTrigger>
        <SelectContent>
          {options.map(([option, text]) => (
            <SelectItem key={option} value={option}>
              {text}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
    </div>
  )
}

function PomodoroSection({ preferences }: { preferences: Preferences }) {
  const update = useUpdatePreferences()
  const [enabled, setEnabled] = useState(preferences.pomodoroEnabled)
  const [focus, setFocus] = useState(String(preferences.pomodoroMinutes))
  const [pause, setPause] = useState(String(preferences.pomodoroBreakMinutes))
  const [autoStop, setAutoStop] = useState(preferences.pomodoroAutoStop)
  const [errors, setErrors] = useState<FormErrors<'focus' | 'pause'>>({})

  function toggle(on: boolean) {
    setEnabled(on)
    // Reminders reach you in another tab only with the browser's permission, asked for while you choose.
    if (on && 'Notification' in window && Notification.permission === 'default') {
      void Notification.requestPermission()
    }
  }

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    const focusMinutes = Number(focus)
    const breakMinutes = Number(pause)
    const validation: FormErrors<'focus' | 'pause'> = {
      focus: Number.isInteger(focusMinutes) && focusMinutes >= 5 && focusMinutes <= 180 ? undefined : 'Choose 5 to 180 minutes.',
      pause: Number.isInteger(breakMinutes) && breakMinutes >= 1 && breakMinutes <= 60 ? undefined : 'Choose 1 to 60 minutes.',
    }
    setErrors(validation)
    if (hasErrors(validation)) {
      return
    }
    try {
      await update.mutateAsync({ ...preferences, pomodoroEnabled: enabled, pomodoroMinutes: focusMinutes, pomodoroBreakMinutes: breakMinutes, pomodoroAutoStop: autoStop })
      toast.success(enabled ? 'Pomodoro is on' : 'Pomodoro settings saved')
    } catch (error) {
      toast.error(errorMessage(error, "Couldn't save the Pomodoro settings."))
    }
  }

  const changed =
    enabled !== preferences.pomodoroEnabled ||
    focus !== String(preferences.pomodoroMinutes) ||
    pause !== String(preferences.pomodoroBreakMinutes) ||
    autoStop !== preferences.pomodoroAutoStop

  return (
    <Section title="Pomodoro" description="Work in focused intervals with short breaks. You get a reminder when each interval ends.">
      <form noValidate onSubmit={handleSubmit} className="grid gap-4">
        <div className="flex items-center justify-between gap-4">
          <Label htmlFor="pomodoro-enabled">Pomodoro reminders</Label>
          <Switch id="pomodoro-enabled" checked={enabled} onCheckedChange={toggle} />
        </div>
        <fieldset disabled={!enabled} className="grid gap-4 disabled:opacity-60">
          <div className="grid gap-4 sm:grid-cols-2">
            <TextField id="pomodoro-focus" label="Focus (minutes)" inputMode="numeric" value={focus} onChange={(e) => setFocus(e.target.value)} error={errors.focus} />
            <TextField id="pomodoro-break" label="Break (minutes)" inputMode="numeric" value={pause} onChange={(e) => setPause(e.target.value)} error={errors.pause} />
          </div>
          <div className="flex items-start justify-between gap-4">
            <div className="grid gap-0.5">
              <Label htmlFor="pomodoro-autostop">Stop the timer when an interval ends</Label>
              <p className="text-muted-foreground text-xs">Then the break counts down in the timer bar.</p>
            </div>
            <Switch id="pomodoro-autostop" checked={autoStop} onCheckedChange={setAutoStop} disabled={!enabled} />
          </div>
        </fieldset>
        <div>
          <Button type="submit" disabled={!changed || update.isPending}>
            Save Pomodoro settings
          </Button>
        </div>
      </form>
    </Section>
  )
}

function PasswordSection() {
  const change = useChangePassword()
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [repeat, setRepeat] = useState('')
  const [errors, setErrors] = useState<FormErrors<'currentPassword' | 'newPassword' | 'repeat'>>({})

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    const validation: FormErrors<'currentPassword' | 'newPassword' | 'repeat'> = {
      currentPassword: current ? undefined : 'Enter your current password.',
      newPassword: next.length >= 8 ? undefined : 'Use at least 8 characters.',
      repeat: repeat === next ? undefined : 'The passwords do not match.',
    }
    setErrors(validation)
    if (hasErrors(validation)) {
      return
    }
    try {
      await change.mutateAsync({ currentPassword: current, newPassword: next })
      setCurrent('')
      setNext('')
      setRepeat('')
      toast.success('Password changed. Your other devices were signed out.')
    } catch (error) {
      setErrors(error instanceof ApiError ? toFormErrors(error, ['currentPassword', 'newPassword', 'repeat'] as const) : { form: CONNECTION_ERROR })
    }
  }

  return (
    <Section title="Password" description="Changing it signs you out on every other device.">
      <form noValidate onSubmit={handleSubmit} className="grid gap-4 sm:max-w-sm">
        {errors.form && (
          <Alert variant="destructive">
            <AlertDescription>{errors.form}</AlertDescription>
          </Alert>
        )}
        <TextField id="password-current" label="Current password" type="password" autoComplete="current-password" value={current} onChange={(e) => setCurrent(e.target.value)} error={errors.currentPassword} />
        <TextField id="password-new" label="New password" type="password" autoComplete="new-password" value={next} onChange={(e) => setNext(e.target.value)} error={errors.newPassword} hint="At least 8 characters. A few words make a strong one." />
        <TextField id="password-repeat" label="Repeat the new password" type="password" autoComplete="new-password" value={repeat} onChange={(e) => setRepeat(e.target.value)} error={errors.repeat} />
        <div>
          <Button type="submit" disabled={change.isPending}>
            Change password
          </Button>
        </div>
      </form>
    </Section>
  )
}

/** An account made with Google or Apple has no password; the reset email sets its first one. */
function SetPasswordSection({ email }: { email: string }) {
  const send = useSendPasswordLink()
  return (
    <Section title="Password" description="You sign in with Google or Apple, so your account has no password. Add one to also sign in with your email address.">
      <div className="grid gap-2">
        <div>
          <Button
            variant="outline"
            disabled={send.isPending || send.isSuccess}
            onClick={() =>
              send.mutate(email, {
                onSuccess: () => toast.success(`We sent a link to ${email}`),
                onError: (error) => toast.error(errorMessage(error, "Couldn't send the email.")),
              })
            }
          >
            {send.isSuccess ? 'Link sent' : 'Email me a link to set a password'}
          </Button>
        </div>
        {send.isSuccess && <p className="text-muted-foreground text-sm">Open the link in the email to choose your password.</p>}
      </div>
    </Section>
  )
}

/** The Google and Apple logins of the account, on a server that offers them. */
function SignInMethodsSection({ email }: { email: string }) {
  const providers = useQuery(serverQuery).data?.signInProviders ?? []
  const logins = useQuery({ ...externalLoginsQuery, enabled: providers.length > 0 })
  const remove = useRemoveExternalLogin()
  if (providers.length === 0) {
    return null
  }

  return (
    <Section title="Google and Apple" description={`Sign in with them instead of a password. Continue with one on the sign-in page with ${email}, and it joins this account.`}>
      <ul aria-label="Sign-in providers" className="divide-y rounded-lg border">
        {providers.map((provider) => {
          const connected = logins.data?.some((login) => login.provider === provider.name)
          return (
            <li key={provider.name} className="flex items-center gap-3 px-3 py-2.5">
              <ProviderLogo name={provider.name} />
              <p className="min-w-0 flex-1 truncate text-sm font-medium">{provider.displayName}</p>
              {logins.isPending ? (
                <Skeleton className="h-8 w-20" />
              ) : connected ? (
                <>
                  <Badge variant="secondary">Connected</Badge>
                  <Button
                    variant="outline"
                    size="sm"
                    disabled={remove.isPending}
                    onClick={() =>
                      remove.mutate(provider.name, {
                        onSuccess: () => toast.success(`You no longer sign in with ${provider.displayName}`),
                        onError: (error) => toast.error(errorMessage(error, `Couldn't remove ${provider.displayName}.`)),
                      })
                    }
                  >
                    Remove
                  </Button>
                </>
              ) : (
                <span className="text-muted-foreground text-xs">Not connected</span>
              )}
            </li>
          )
        })}
      </ul>
    </Section>
  )
}

function SessionsSection() {
  const { timeZone, dateFormat, hour12 } = useWorkspace()
  const sessions = useQuery(sessionsQuery)
  const revoke = useRevokeSession()
  const revokeOthers = useRevokeOtherSessions()
  const others = sessions.data?.filter((s) => !s.isCurrent) ?? []

  const when = (instant: string) => `${formatDayAs(dayKey(instant, timeZone), dateFormat)}, ${formatClock(instant, timeZone, hour12)}`

  return (
    <Section
      title="Where you're signed in"
      description="Sign out of a device or agent you no longer use or do not recognize. Agents connected over MCP, such as Claude Code or Codex, are listed here too."
    >
      <div className="grid gap-3">
        {sessions.isPending && <Skeleton className="h-14 w-full" />}
        {sessions.isError && <p className="text-muted-foreground text-sm">{errorMessage(sessions.error, "Couldn't load your devices.")}</p>}
        {!!sessions.data?.length && (
          <ul aria-label="Signed-in devices" className="divide-y rounded-lg border">
            {sessions.data.map((device) => (
              <li key={device.id} className="flex items-center gap-3 px-3 py-2.5">
                {device.agent ? (
                  <Bot aria-hidden="true" className="text-muted-foreground size-4 shrink-0" />
                ) : (
                  <Laptop aria-hidden="true" className="text-muted-foreground size-4 shrink-0" />
                )}
                <div className="min-w-0 flex-1">
                  <p className="flex items-center gap-2 truncate text-sm font-medium">
                    {device.deviceName ?? 'Unknown device'}
                    {device.isCurrent && <Badge variant="secondary">This device</Badge>}
                  </p>
                  {device.agent && (
                    <p className="text-muted-foreground truncate text-xs">
                      Agent · {device.agent.canWrite ? 'reads and proposes' : 'reads'} in {device.agent.workspaceName}
                    </p>
                  )}
                  <p className="text-muted-foreground truncate text-xs">
                    {device.agent ? 'Connected' : 'Signed in'} {when(device.signedInAt)} · last active {when(device.lastActiveAt)}
                  </p>
                </div>
                {!device.isCurrent && (
                  <Button
                    variant="outline"
                    size="sm"
                    disabled={revoke.isPending}
                    onClick={() =>
                      revoke.mutate(device.id, {
                        onSuccess: () => toast.success(`Signed out of ${device.deviceName ?? 'the device'}`),
                        onError: (error) => toast.error(errorMessage(error, "Couldn't sign the device out.")),
                      })
                    }
                  >
                    Sign out
                  </Button>
                )}
              </li>
            ))}
          </ul>
        )}
        {others.length > 1 && (
          <div>
            <Button
              variant="outline"
              disabled={revokeOthers.isPending}
              onClick={() =>
                revokeOthers.mutate(undefined, {
                  onSuccess: () => toast.success('Signed out of every other device'),
                  onError: (error) => toast.error(errorMessage(error, "Couldn't sign the devices out.")),
                })
              }
            >
              Sign out of all other devices
            </Button>
          </div>
        )}
      </div>
    </Section>
  )
}

function AgentActivitySection({ me }: { me: Me }) {
  const update = useUpdateAgentActivitySettings()
  const settings = me.agentActivity ?? { sync: false, titles: false }

  async function change(sync: boolean, titles: boolean) {
    try {
      await update.mutateAsync({ sync, titles })
      toast.success(sync ? (titles ? 'Session titles will sync' : 'Agent activity will sync') : 'Agent activity sync is off; what was uploaded is deleted')
    } catch (error) {
      toast.error(errorMessage(error, "Couldn't change agent activity sync."))
    }
  }

  return (
    <Section
      title="Agent activity"
      description="The Mac app can read Claude Code and Codex logs to suggest splitting an entry between projects. By default that stays on the Mac."
    >
      <div className="grid gap-4">
        <div className="flex items-start justify-between gap-4">
          <div className="grid gap-0.5">
            <Label htmlFor="agent-activity-sync">Sync agent activity from your computers</Label>
            <p className="text-muted-foreground text-xs">
              So the web suggests splits too. Spindle on your Mac and Windows computers uploads when you sent prompts, when agents were busy, and each repository's name, git
              origin and branches. Folder paths, prompts and answers never leave the computer. Only you can see it, and turning this off deletes what
              was uploaded.
            </p>
          </div>
          <Switch id="agent-activity-sync" checked={settings.sync} disabled={update.isPending} onCheckedChange={(on) => void change(on, false)} />
        </div>
        <div className="flex items-start justify-between gap-4">
          <div className="grid gap-0.5">
            <Label htmlFor="agent-activity-titles" className={settings.sync ? undefined : 'opacity-60'}>
              Include session titles
            </Label>
            <p className="text-muted-foreground text-xs">A session's title can be the first line of a prompt. Splits work without them.</p>
          </div>
          <Switch
            id="agent-activity-titles"
            checked={settings.titles}
            disabled={!settings.sync || update.isPending}
            onCheckedChange={(on) => void change(true, on)}
          />
        </div>
        <p className="text-muted-foreground text-xs">The projects you choose for repositories are kept with your account either way, so your computers and the web agree.</p>
      </div>
    </Section>
  )
}

function DataSection() {
  return (
    <Section title="Your data" description="A copy of your profile and every time entry you tracked, in every workspace, and any agent activity you synced, as a JSON file.">
      <DownloadDataButton />
    </Section>
  )
}

function DeleteSection() {
  return (
    <Section
      title="Delete account"
      description="Workspaces only you use are deleted with everything in them. In workspaces you share, your time stays for the team's reports, without your name."
      destructive
    >
      <DeleteAccountButton />
    </Section>
  )
}
