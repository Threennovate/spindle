export const OPEN_REVIEW_EVENT = 'spindle:open-review'

/** The days a review covers: one day, or a calendar week. */
export interface ReviewScope {
  days: string[]
}

/** Opens the day's (or week's) review from anywhere, e.g. a day's header or the calendar. */
export function openReview(scope: ReviewScope) {
  window.dispatchEvent(new CustomEvent<ReviewScope>(OPEN_REVIEW_EVENT, { detail: scope }))
}
