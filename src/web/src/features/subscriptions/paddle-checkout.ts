import { CheckoutEventNames, initializePaddle, type Paddle, type PaddleEventData } from '@paddle/paddle-js'
import type { CheckoutStart } from '@/api/types'

let loaded: { key: string; paddle: Promise<Paddle | undefined> } | undefined
let onEvent: ((event: PaddleEventData) => void) | undefined

/**
 * Paddle.js, loaded once per environment and token, the first time someone opens a checkout. It is told the Paddle
 * customer the checkout is for, which Paddle Retain needs: Paddle's `ctm_` ID, never ours or an email.
 */
async function paddleFor(start: CheckoutStart) {
  const key = `${start.environment}:${start.clientToken}`
  const pwCustomer = { id: start.customerId }
  if (loaded?.key !== key) {
    loaded = {
      key,
      paddle: initializePaddle({
        environment: start.environment === 'production' ? 'production' : 'sandbox',
        token: start.clientToken,
        pwCustomer,
        eventCallback: (event) => onEvent?.(event),
      }),
    }
    return loaded.paddle
  }
  const paddle = await loaded.paddle
  paddle?.Update({ pwCustomer })
  return paddle
}

/**
 * Opens Paddle's checkout for a transaction the server prepared, and resolves with whether it was paid: true once the
 * checkout completes, false if it is closed first. The subscription itself reaches the server by webhook.
 */
export async function openPaddleCheckout(start: CheckoutStart): Promise<boolean> {
  const paddle = await paddleFor(start)
  if (!paddle) throw new Error('Paddle’s checkout couldn’t load.')

  return new Promise((resolve) => {
    let completed = false
    onEvent = (event) => {
      if (event.name === CheckoutEventNames.CHECKOUT_COMPLETED) {
        completed = true
        // Leaves Paddle's thank-you message up for a moment before closing.
        window.setTimeout(() => paddle.Checkout.close(), 1500)
      }
      if (event.name === CheckoutEventNames.CHECKOUT_CLOSED) {
        onEvent = undefined
        resolve(completed)
      }
    }
    paddle.Checkout.open({ transactionId: start.transactionId, settings: { displayMode: 'overlay', variant: 'one-page' } })
  })
}
