extends SceneTree


func _initialize() -> void:
	var args := OS.get_cmdline_user_args()
	if args.size() != 2:
		push_error("Usage: -- <source_manifest_path> <output_pck_path>")
		quit(1)
		return

	var source_manifest := args[0]
	var output_pck := args[1]
	if not FileAccess.file_exists(source_manifest):
		push_error("Manifest does not exist: %s" % source_manifest)
		quit(1)
		return

	var packer := PCKPacker.new()
	var error := packer.pck_start(output_pck)
	if error != OK:
		push_error("Failed to start PCK packer: %s" % error)
		quit(1)
		return

	error = packer.add_file("res://mod_manifest.json", source_manifest)
	if error != OK:
		push_error("Failed to add mod_manifest.json: %s" % error)
		quit(1)
		return

	error = packer.flush()
	if error != OK:
		push_error("Failed to finalize PCK: %s" % error)
		quit(1)
		return

	print("Packed %s" % output_pck)
	quit(0)

