-- Allowed hook: button_01 OnPressed opens door_01.
-- Only engine Fire calls; no shell, no file access, no secrets.
function OnPressed()
  Fire("door_01", "Open")
end
