use std::path::PathBuf;

use clap::{Parser, Subcommand};
use fc_image_builder::build::BuildRequest;
use fc_image_builder::{
    CandidatePackageLock, ImageManifest, RecipeFile, RunDirectory, build_qualified_image,
    compile_pinned_cli, fetch_qualified_inputs, prepare_cli_inputs, qualify_apt_inputs,
    verify_cli_compile_receipt, write_root_tree_inventory,
};

#[derive(Debug, Parser)]
#[command(name = "fc-image-builder", version, about)]
struct Cli {
    #[command(subcommand)]
    command: Command,
}

#[derive(Debug, Subcommand)]
enum Command {
    /// Parse and validate immutable candidate identities without fetching data.
    ValidateRecipe {
        /// Versioned TOML recipe.
        #[arg(long)]
        recipe: PathBuf,
    },
    /// Resolve the checked-in research packet into an explicitly unverified candidate lock.
    Lock {
        /// Versioned TOML recipe.
        #[arg(long)]
        recipe: PathBuf,
        /// Package closure to materialize as an unverified candidate lock.
        #[arg(long, value_parser = ["guest", "builder"])]
        group: String,
        /// Existing caller-owned cache directory for the lock receipt.
        #[arg(long)]
        cache_root: PathBuf,
        /// Unique, filename-safe run identifier.
        #[arg(long)]
        run_id: String,
    },
    /// Verify source readiness and package-closure identities without fetching inputs.
    Resolve {
        /// Versioned TOML recipe.
        #[arg(long)]
        recipe: PathBuf,
    },
    /// Resolve and verify the pinned Rust CLI compiler support inputs.
    PrepareCliInputs {
        #[arg(long)]
        recipe: PathBuf,
        #[arg(long)]
        output_root: PathBuf,
        #[arg(long)]
        run_id: String,
    },
    /// Compile the locked CLI offline from a reviewed compiler provision receipt.
    CompilePinnedCli {
        #[arg(long)]
        recipe: PathBuf,
        #[arg(long)]
        provision_receipt: PathBuf,
        #[arg(long)]
        output_root: PathBuf,
        #[arg(long)]
        run_id: String,
    },
    /// Verify the running executable against a pinned offline compile receipt.
    VerifyCliCompileReceipt {
        #[arg(long)]
        recipe: PathBuf,
        #[arg(long)]
        compile_receipt: PathBuf,
    },
    /// Fetch is fail-closed until signed sources and apt's exact closure are verified.
    Fetch {
        /// Versioned TOML recipe.
        #[arg(long)]
        recipe: PathBuf,
        /// Guest package lock produced by the pinned apt solver.
        #[arg(long)]
        guest_lock: PathBuf,
        /// Ubuntu builder package lock produced by the pinned apt solver.
        #[arg(long)]
        builder_lock: PathBuf,
        /// Pinned, self-contained OCI archive of the derived Noble builder.
        #[arg(long)]
        derived_builder_archive: PathBuf,
        /// Receipt pinning this exact running CLI executable.
        #[arg(long)]
        cli_compile_receipt: PathBuf,
        /// Existing caller-owned cache directory for the fetch run.
        #[arg(long)]
        cache_root: PathBuf,
        /// Unique, filename-safe run identifier.
        #[arg(long)]
        run_id: String,
    },
    /// Verify Canonical and signed APT inputs, solve both Noble closures, and build a derived
    /// offline Ubuntu builder. The resulting receipt stays a candidate until separately reviewed.
    QualifyAptInputs {
        /// Versioned candidate TOML recipe.
        #[arg(long)]
        recipe: PathBuf,
        /// Existing caller-owned cache/output directory; all bootstrap outputs stay below it.
        #[arg(long)]
        output_root: PathBuf,
        /// Unique, filename-safe run identifier.
        #[arg(long)]
        run_id: String,
    },
    /// Build is fail-closed until all signed inputs and builder tools are locked.
    Build {
        /// Versioned TOML recipe.
        #[arg(long)]
        recipe: PathBuf,
        /// Guest package lock produced by the pinned apt solver.
        #[arg(long)]
        guest_lock: PathBuf,
        /// Ubuntu builder package lock produced by the pinned apt solver.
        #[arg(long)]
        builder_lock: PathBuf,
        /// Pinned, self-contained OCI archive of the derived Noble builder.
        #[arg(long)]
        derived_builder_archive: PathBuf,
        /// Receipt pinning this exact running CLI executable.
        #[arg(long)]
        cli_compile_receipt: PathBuf,
        /// Completed Fetch run directory containing fetch-receipt.json.
        #[arg(long)]
        input_dir: PathBuf,
        /// Existing caller-owned directory for the new build run.
        #[arg(long)]
        output_root: PathBuf,
        /// Unique, filename-safe build run identifier.
        #[arg(long)]
        run_id: String,
    },
    /// Recompute every image, inventory and command-log hash in a provenance receipt.
    Verify {
        /// Image manifest JSON path.
        #[arg(long)]
        manifest: PathBuf,
    },
    /// Internal helper invoked inside the pinned builder to inventory a guest root tree.
    #[command(hide = true)]
    InventoryRootfs {
        #[arg(long)]
        root: PathBuf,
        #[arg(long)]
        output: PathBuf,
    },
}

fn main() {
    if let Err(error) = run() {
        eprintln!("fc-image-builder: {error}");
        std::process::exit(1);
    }
}

#[allow(clippy::too_many_lines)] // Each CLI phase keeps its own fail-closed boundary beside its arguments.
fn run() -> Result<(), Box<dyn std::error::Error>> {
    let cli = Cli::parse();
    match cli.command {
        Command::ValidateRecipe { recipe } => {
            let recipe = RecipeFile::read(&recipe)?;
            println!("recipe_identity={}", recipe.recipe().identity);
            println!("recipe_version={}", recipe.recipe().recipe_version);
            println!("recipe_sha256={}", recipe.sha256());
            println!("ext4_uuid={}", recipe.filesystem_uuid());
            let gaps = recipe.unresolved_inputs();
            if gaps.is_empty() {
                println!("readiness=locked-inputs-present");
            } else {
                println!("readiness=candidate-incomplete");
                for gap in gaps.as_slice() {
                    println!("unresolved={gap}");
                }
            }
        }
        Command::Lock {
            recipe,
            group,
            cache_root,
            run_id,
        } => {
            let recipe = RecipeFile::read(&recipe)?;
            let packet_path =
                recipe.resolve_recipe_path(&recipe.recipe().guest_apt.candidate_packet_path)?;
            let candidate =
                CandidatePackageLock::from_research_packet(&recipe, &packet_path, &group)?;
            let run = RunDirectory::create(&cache_root, &run_id)?;
            let path = candidate.write_new(&run)?;
            println!("lock_status={}", candidate.qualification_status);
            println!("recipe_sha256={}", recipe.sha256());
            if let Some(sha256) = &candidate.research_packet_sha256 {
                println!("research_packet_sha256={sha256}");
            }
            println!("apt_indexes={}", candidate.indexes.len());
            println!("package_role={}", candidate.package_role);
            println!("locked_packages={}", candidate.packages.len());
            println!("lock_path={path}");
            for limitation in &candidate.limitations {
                println!("limitation={limitation}");
            }
        }
        Command::Resolve { recipe } => {
            let recipe = RecipeFile::read(&recipe)?;
            let packet_path =
                recipe.resolve_recipe_path(&recipe.recipe().guest_apt.candidate_packet_path)?;
            let guest = CandidatePackageLock::from_research_packet(&recipe, &packet_path, "guest")?;
            let builder =
                CandidatePackageLock::from_research_packet(&recipe, &packet_path, "builder")?;
            println!("recipe_identity={}", recipe.recipe().identity);
            println!("recipe_sha256={}", recipe.sha256());
            println!(
                "kernel_source_url={}",
                recipe.recipe().kernel.source_archive_url
            );
            println!(
                "kernel_source_version={}",
                recipe.recipe().kernel.source_version
            );
            println!("ubuntu_base_url={}", recipe.recipe().ubuntu_base.url);
            println!("apt_snapshot={}", recipe.recipe().guest_apt.snapshot_url);
            println!("apt_indexes={}", guest.indexes.len());
            println!("guest_candidate_packages={}", guest.packages.len());
            println!("builder_candidate_packages={}", builder.packages.len());
            println!("readiness=candidate-incomplete");
            for gap in recipe.unresolved_inputs().as_slice() {
                println!("unresolved={gap}");
            }
            for limitation in guest.limitations.iter().chain(&builder.limitations) {
                println!("limitation={limitation}");
            }
        }
        Command::PrepareCliInputs {
            recipe,
            output_root,
            run_id,
        } => {
            let recipe = RecipeFile::read(&recipe)?;
            let path = prepare_cli_inputs(&recipe, &output_root, &run_id)?;
            println!("provision_status=candidate-unqualified");
            println!("recipe_sha256={}", recipe.sha256());
            println!("provision_receipt={}", path.display());
        }
        Command::CompilePinnedCli {
            recipe,
            provision_receipt,
            output_root,
            run_id,
        } => {
            let recipe = RecipeFile::read(&recipe)?;
            let path = compile_pinned_cli(&recipe, &provision_receipt, &output_root, &run_id)?;
            println!("compile_status=candidate-unqualified");
            println!("recipe_sha256={}", recipe.sha256());
            println!("compile_receipt={}", path.display());
        }
        Command::VerifyCliCompileReceipt {
            recipe,
            compile_receipt,
        } => {
            let recipe = RecipeFile::read(&recipe)?;
            let executable = std::env::current_exe()?;
            let identity = verify_cli_compile_receipt(&recipe, &compile_receipt, &executable)?;
            println!("cli_verification=passed");
            println!("cli_source_tree_sha256={}", identity.source_tree_sha256);
            println!("cli_executable_sha256={}", identity.output_sha256);
            println!("cli_host_abi={}", identity.host_abi);
        }
        Command::Fetch {
            recipe,
            guest_lock,
            builder_lock,
            derived_builder_archive,
            cli_compile_receipt,
            cache_root,
            run_id,
        } => {
            let recipe = RecipeFile::read(&recipe)?;
            let guest = CandidatePackageLock::read(&guest_lock, &recipe)?;
            let builder = CandidatePackageLock::read(&builder_lock, &recipe)?;
            let path = fetch_qualified_inputs(
                &recipe,
                &guest,
                &builder,
                &derived_builder_archive,
                &cli_compile_receipt,
                &cache_root,
                &run_id,
            )?;
            println!("fetch_status=verified-inputs");
            println!("recipe_sha256={}", recipe.sha256());
            println!("fetch_receipt={}", path.display());
        }
        Command::QualifyAptInputs {
            recipe,
            output_root,
            run_id,
        } => {
            let recipe = RecipeFile::read(&recipe)?;
            let path = qualify_apt_inputs(&recipe, &output_root, &run_id)?;
            println!("qualification_status=apt-inputs-verified-candidate");
            println!("recipe_sha256={}", recipe.sha256());
            println!("qualification_receipt={}", path.display());
        }
        Command::Build {
            recipe,
            guest_lock,
            builder_lock,
            derived_builder_archive,
            cli_compile_receipt,
            input_dir,
            output_root,
            run_id,
        } => {
            let recipe = RecipeFile::read(&recipe)?;
            let guest = CandidatePackageLock::read(&guest_lock, &recipe)?;
            let builder = CandidatePackageLock::read(&builder_lock, &recipe)?;
            let path = build_qualified_image(BuildRequest {
                recipe: &recipe,
                guest: &guest,
                builder: &builder,
                derived_builder_archive: &derived_builder_archive,
                cli_compile_receipt: &cli_compile_receipt,
                input_dir: &input_dir,
                output_root: &output_root,
                run_id: &run_id,
            })?;
            println!("build_status=single-build-unqualified");
            println!("recipe_sha256={}", recipe.sha256());
            println!("image_manifest={}", path.display());
        }
        Command::Verify { manifest } => {
            let image = ImageManifest::read(&manifest)?;
            image.verify_outputs(&manifest)?;
            println!("verification=passed");
            println!("recipe_identity={}", image.recipe.identity);
            println!("recipe_sha256={}", image.recipe.sha256);
            println!("kernel_sha256={}", image.kernel.output.sha256);
            println!("rootfs_sha256={}", image.rootfs.output.sha256);
            println!("reproducibility={:?}", image.reproducibility_status);
        }
        Command::InventoryRootfs { root, output } => {
            write_root_tree_inventory(&root, &output)?;
            println!("inventory_status=written");
            println!("inventory={}", output.display());
        }
    }
    Ok(())
}
