import Observation

/// Re-runs `apply` whenever any observable property it reads changes.
/// Used by AppKit coordinators that need to react to `@Observable` state.
@MainActor
func observeContinuously(_ apply: @escaping @MainActor () -> Void) {
    withObservationTracking {
        apply()
    } onChange: {
        Task { @MainActor in
            observeContinuously(apply)
        }
    }
}
