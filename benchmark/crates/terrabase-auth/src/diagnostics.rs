use std::time::Instant;

pub(crate) struct Timer {
    stage: &'static str,
    started: Instant,
    enabled: bool,
}

impl Timer {
    pub(crate) fn new(stage: &'static str) -> Self {
        Self {
            stage,
            started: Instant::now(),
            enabled: std::env::var_os("TERRASCALE_AUTH_TIMING").is_some(),
        }
    }
}

impl Drop for Timer {
    fn drop(&mut self) {
        if self.enabled {
            let elapsed = self.started.elapsed();
            if elapsed >= std::time::Duration::from_millis(10) {
                eprintln!("[auth timing] stage={} elapsed_us={}", self.stage, elapsed.as_micros());
            }
        }
    }
}
