import { useState } from 'preact/hooks';

export function Pricing() {
	const [annual, setAnnual] = useState(false);
	const plans = [
		{
			name: 'Trial',
			icon: '🚀',
			price: 'Free',
			annualPrice: 'Free',
			features: [
				{ label: 'Databases', value: 'Unlimited', note: '**' },
				{ label: 'Database Operations', value: '1 million operations', note: '*' },
				{ label: 'Storage', value: '1 GB', note: '*' },
				{ label: 'Egress Bandwidth', value: '1 GB', note: '*' },
				{ label: 'Support', value: 'Low Priority' },
			],
			cta: 'Create account',
			ctaIcon: 'icon-[mdi--account-plus]',
			color: 'bg-base-100',
			border: 'border-base-200',
		},
		{
			name: 'Hobby',
			icon: '🛠️',
			price: '$4.99',
			annualPrice: '$49',
			features: [
				{ label: 'Databases', value: 'Unlimited', note: '**' },
				{ label: 'Database Operations', value: '10 million included' },
				{ label: 'Storage', value: '10 GB' },
				{ label: 'Egress Bandwidth', value: '15 GB' },
				{ label: 'Support', value: 'Customer-only Discord' },
			],
			cta: 'Get Hobby',
			ctaIcon: 'icon-[mdi--rocket-launch-outline]',
			color: 'bg-base-100',
			border: 'border-base-200',
		},
		{
			name: 'Pro',
			icon: '💼',
			price: '$24.99',
			annualPrice: '$249',
			features: [
				{ label: 'Databases', value: 'Unlimited', note: '**' },
				{ label: 'Database Operations', value: '1 billion included' },
				{ label: 'Storage', value: '50 GB' },
				{ label: 'Egress Bandwidth', value: '100 GB' },
				{ label: 'Support', value: 'Customer-only Discord' },
			],
			cta: 'Get Pro',
			ctaIcon: 'icon-[mdi--star-outline]',
			color: 'bg-gradient-to-br from-primary to-secondary text-white',
			border: 'border-4 border-primary',
			highlight: true,
		},
		{
			name: 'Enterprise',
			icon: '🏢',
			price: '$99.99',
			annualPrice: '$999',
			features: [
				{ label: 'Databases', value: 'Unlimited', note: '**' },
				{ label: 'Support', value: 'Premium' },
				{ label: 'Storage', value: '$0.25 per GB' },
				{ label: 'Egress Bandwidth', value: '$0.05 per GB', note: '***' },
				{ label: 'Database Operations', value: '$1.00 per 1 billion operations' },
			],
			cta: 'Get enterprise',
			ctaIcon: 'icon-[mdi--email-outline]',
			color: 'bg-base-100',
			border: 'border-base-200',
		},
	];

	// Collect all unique features for table rows
	const allFeatures = Array.from(
		new Set(plans.flatMap(plan => plan.features.map(f => f.label)))
	);

	return (
		<div className="max-w-6xl mx-auto px-4 py-20 bg-gradient-to-br from-primary/10 to-secondary/10 rounded-3xl shadow-2xl mt-10 animate-fade-in">
			<h1 className="text-3xl sm:text-4xl lg:text-5xl font-extrabold mb-6 sm:mb-10 text-center bg-gradient-to-r from-primary to-secondary bg-clip-text text-transparent animate-fade-in-down">Pricing Plans</h1>
			<div className="flex justify-center mb-8 sm:mb-12 animate-fade-in-up">
				<button
					className={`btn btn-outline rounded-full px-4 sm:px-8 mr-2 sm:mr-4 text-xs sm:text-base ${!annual ? 'btn-primary' : ''}`}
					onClick={() => setAnnual(false)}
				>
					Monthly
				</button>
				<button
					className={`btn btn-outline rounded-full px-4 sm:px-8 text-xs sm:text-base ${annual ? 'btn-primary' : ''}`}
					onClick={() => setAnnual(true)}
				>
					Annual <span className="ml-1 sm:ml-2 text-xs text-success">(Save 20%)</span>
				</button>
			</div>

			{/* Mobile Card Layout */}
			<div className="block lg:hidden animate-fade-in-up mb-8">
				<div className="grid gap-6">
					{plans.map((plan, idx) => (
						<div key={plan.name} className={`card ${plan.color} ${plan.border} shadow-xl ${plan.highlight ? 'ring-2 ring-primary' : ''}`}>
							<div className="card-body p-6">
								{/* Plan Header */}
								<div className="text-center mb-4">
									<div className="text-4xl mb-2">{plan.icon}</div>
									<h3 className="text-2xl font-bold">{plan.name}</h3>
									{plan.highlight && (
										<div className="badge badge-primary badge-sm mt-1">POPULAR</div>
									)}
								</div>
								
								{/* Price */}
								<div className="text-center mb-6">
									<div className="text-3xl font-extrabold">
										{annual ? plan.annualPrice : plan.price}
										{plan.price !== 'Free' && <span className="text-base font-normal ml-1">{annual ? '/yr' : '/mo'}</span>}
									</div>
								</div>

								{/* Features */}
								<div className="space-y-3 mb-6">
									{plan.features.map((feature, i) => (
										<div key={i} className="flex justify-between items-center py-1 border-b border-base-200/50 last:border-b-0">
											<span className="font-medium text-sm">{feature.label}</span>
											<span className="text-sm text-base-content/80">
												{feature.value} {feature.note && <span className="text-xs text-base-content/60">{feature.note}</span>}
											</span>
										</div>
									))}
								</div>

								{/* CTA Button */}
								<a
									href="#"
									className={`btn btn-lg w-full rounded-full flex items-center justify-center gap-2 ${plan.name === 'Pro' ? 'btn-primary' : plan.name === 'Trial' ? 'btn-outline font-semibold' : 'btn-outline'}`}
								>
									<span className={plan.ctaIcon}></span> {plan.cta}
								</a>
							</div>
						</div>
					))}
				</div>
			</div>

			{/* Desktop Table Layout */}
			<div className="hidden lg:block overflow-x-auto animate-fade-in-up mb-8">
				<table className="table table-zebra table-lg w-full rounded-2xl shadow-xl bg-base-100">
					<thead>
						<tr>
							<th className="bg-base-200"></th>
							{plans.map((plan, idx) => (
								<th key={plan.name} className={`text-center text-xl font-bold ${plan.highlight ? 'bg-gradient-to-br from-primary to-secondary text-white' : 'bg-base-200'}`}
									style={plan.highlight ? { borderTop: '4px solid var(--fallback-bc, #570df8)' } : {}}>
									<div className="flex flex-col items-center gap-2">
										<span className="text-4xl">{plan.icon}</span>
										<span>{plan.name}</span>
									</div>
								</th>
							))}
						</tr>
						<tr>
							<th className="bg-base-200">Price</th>
							{plans.map((plan, idx) => (
								<td key={plan.name} className="text-center text-2xl font-extrabold">
									{annual ? plan.annualPrice : plan.price}
									{plan.price !== 'Free' && <span className="text-base font-normal ml-1">{annual ? '/yr' : '/mo'}</span>}
								</td>
							))}
						</tr>
					</thead>
					<tbody>
						{allFeatures.map((feature, i) => (
							<tr key={feature}>
								<th className="bg-base-200 text-base font-semibold text-left">{feature}</th>
								{plans.map((plan, idx) => {
									const f = plan.features.find(f => f.label === feature);
									return (
										<td key={plan.name} className="text-center text-base-content/80">
											{f ? <span>{f.value} {f.note && <span className="text-xs text-base-content/60">{f.note}</span>}</span> : <span className="opacity-30">—</span>}
										</td>
									);
								})}
							</tr>
						))}
						<tr>
							<th className="bg-base-200"></th>
							{plans.map((plan, idx) => (
								<td key={plan.name} className="text-center">
									<a
										href="#"
										className={`btn btn-lg w-full rounded-full flex items-center justify-center gap-2 mt-2 ${plan.name === 'Pro' ? 'btn-primary' : plan.name === 'Trial' ? 'btn-outline px-8 py-3 font-semibold' : 'btn-outline'}`}
									>
										<span className={plan.ctaIcon}></span> {plan.cta}
									</a>
								</td>
							))}
						</tr>
					</tbody>
				</table>
			</div>
			<div className="bg-base-100 rounded-xl shadow-md p-4 sm:p-8 text-center text-base-content/70 text-xs sm:text-sm animate-fade-in-up">
				<ul className="list-disc list-inside space-y-1">
					<li>* Usage limits reset monthly.</li>
					<li>** Unlimited databases per account.</li>
					<li>*** Egress overages billed at $0.05/GB.</li>
				</ul>
			</div>

			{/* FAQ Section */}
			<div className="max-w-4xl mx-auto mt-12 sm:mt-16 mb-8 animate-fade-in-up">
				<h2 className="text-2xl sm:text-3xl font-bold mb-4 sm:mb-6 text-center">Frequently Asked Questions</h2>
				<div className="join join-vertical w-full">
					<div className="collapse collapse-arrow join-item border border-base-200 bg-base-100">
						<input type="checkbox" />
						<div className="collapse-title text-lg sm:text-xl font-medium">
							What is a Database Operation?
						</div>
						<div className="collapse-content text-sm sm:text-base text-base-content/80">
							<p>A database operation refers to any interaction with your database. This includes:</p>
							<ul className="list-disc ml-4 sm:ml-6 my-2">
								<li><b>Read Operations:</b> When you retrieve data from the database, it counts as one read operation.</li>
								<li><b>Write Operations:</b> When you save or modify data in the database, it counts as one write operation.</li>
								<li><b>Replication Operations:</b> If your database is configured to replicate data across multiple regions, each time an object is written or updated, it incurs an operation for the initial write plus one operation for each region it replicates to. For example, if you have a database that replicates to 10 regions, a single write to the database will cost 10 operations (1 for the initial write + 9 for replication).</li>
							</ul>
						</div>
					</div>
					<div className="collapse collapse-arrow join-item border border-base-200 bg-base-100">
						<input type="checkbox" />
						<div className="collapse-title text-lg sm:text-xl font-medium">
							What if I need more operations or storage?
						</div>
						<div className="collapse-content text-sm sm:text-base text-base-content/80">
							<p>You can easily <b>upgrade</b> your plan at any time from your account dashboard. For instance, if you're on the Pro plan and need more than 50 GB of storage or 1 billion operations, you can upgrade to the <b>Pay-as-you-go Enterprise</b> plan. This lets you scale your resources to fit your needs as your project grows.</p>
						</div>
					</div>
					<div className="collapse collapse-arrow join-item border border-base-200 bg-base-100">
						<input type="checkbox" />
						<div className="collapse-title text-lg sm:text-xl font-medium">
							When can I change my subscription?
						</div>
						<div className="collapse-content text-sm sm:text-base text-base-content/80">
							<p>You can change your subscription anytime you want.</p>
							<ul className="list-disc ml-4 sm:ml-6 my-2">
								<li><b>Upgrading:</b> When you upgrade, your new plan's features will be available immediately. We'll adjust the cost for your current billing cycle on a prorated basis.</li>
								<li><b>Downgrading:</b> If you choose to downgrade, your current plan will stay active until the end of your billing cycle. Your new, lower-cost plan will then start at the beginning of the next cycle.</li>
							</ul>
							<p className="mt-2">Please note that we generally don't offer partial refunds for downgrades. However, we're happy to review special cases, so feel free to contact our support team to discuss your specific situation.</p>
						</div>
					</div>
				</div>
			</div>
		</div>
	);
}
