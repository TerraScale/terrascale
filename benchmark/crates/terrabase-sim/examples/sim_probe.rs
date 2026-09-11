//! Local debugging probe (not shipped): print per-seed simulation reports.
fn main() {
    for seed in terrabase_sim::rng::seed_batch(7, 4) {
        let report = terrabase_sim::check::run_scenario(terrabase_sim::SimConfig::with_seed(seed));
        println!(
            "seed {seed}: quiescent={} converged={} committed={} digested={}",
            report.quiescent,
            report.converged,
            report.committed,
            report.digest
        );
    }
}
