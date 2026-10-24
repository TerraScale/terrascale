# API key usage during deletion

API key usage records belong to their organization and, when present, their project. Durable deletion jobs remove and verify these records alongside the keys, including records left behind by an interrupted cleanup.

Usage writes check the same organization and project deletion fences as key writes. A token exchange already in flight cannot recreate usage metadata after that scope is deleted. Recording usage remains best effort and grants no access.
